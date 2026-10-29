using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal static class LobbyTcpForwarder
{
    private static int Main(string[] args)
    {
        if (args == null || args.Length != 3)
        {
            Console.Error.WriteLine("Usage: LobbyTcpForwarder.exe <listenPort> <targetPort> <readyFile>");
            return 2;
        }

        int listenPort;
        int targetPort;
        if (!int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out listenPort)
            || listenPort < 1 || listenPort > 65535)
        {
            Console.Error.WriteLine("listenPort must be an integer from 1 to 65535");
            return 2;
        }
        if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out targetPort)
            || targetPort < 1 || targetPort > 65535)
        {
            Console.Error.WriteLine("targetPort must be an integer from 1 to 65535");
            return 2;
        }
        if (listenPort == targetPort)
        {
            Console.Error.WriteLine("listenPort and targetPort must differ; forwarding to the same port would loop");
            return 2;
        }
        if (string.IsNullOrEmpty(args[2]))
        {
            Console.Error.WriteLine("readyFile is required");
            return 2;
        }

        try
        {
            using (Forwarder forwarder = new Forwarder(listenPort, targetPort, Path.GetFullPath(args[2])))
            {
                forwarder.Run();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LobbyTcpForwarder failed: " + ex.Message);
            return 1;
        }
    }

    private sealed class Forwarder : IDisposable
    {
        private readonly int listenPort;
        private readonly int targetPort;
        private readonly string readyPath;
        private readonly string readyContents;
        private readonly TcpListener listener;
        private readonly object gate = new object();
        private readonly HashSet<ForwardSession> sessions = new HashSet<ForwardSession>();
        private int stopping;
        private bool readyCreated;

        public Forwarder(int listenPort, int targetPort, string readyPath)
        {
            this.listenPort = listenPort;
            this.targetPort = targetPort;
            this.readyPath = readyPath;
            readyContents = "LISTENING=127.0.0.1:" + listenPort.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine + "OWNER=" + Guid.NewGuid().ToString("N") + Environment.NewLine;
            listener = new TcpListener(IPAddress.Loopback, listenPort);
        }

        public void Run()
        {
            ConsoleCancelEventHandler cancelHandler = delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Stop();
            };
            EventHandler exitHandler = delegate { Stop(); };

            listener.Start(16);
            try
            {
                CreateReadyFile();
                Console.CancelKeyPress += cancelHandler;
                AppDomain.CurrentDomain.ProcessExit += exitHandler;
                Console.WriteLine("LISTENING 127.0.0.1:" + listenPort + " -> 127.0.0.1:" + targetPort);
                while (Volatile.Read(ref stopping) == 0)
                {
                    TcpClient incoming;
                    try
                    {
                        incoming = listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        if (Volatile.Read(ref stopping) != 0) break;
                        throw;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (Volatile.Read(ref stopping) != 0) break;
                        throw;
                    }
                    StartSession(incoming);
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                AppDomain.CurrentDomain.ProcessExit -= exitHandler;
                Stop();
            }
        }

        private void CreateReadyFile()
        {
            string directory = Path.GetDirectoryName(readyPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            byte[] bytes = new UTF8Encoding(false).GetBytes(readyContents);
            string temporaryPath = readyPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                File.Move(temporaryPath, readyPath);
                readyCreated = true;
            }
            finally { try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { } }
        }

        private void StartSession(TcpClient incoming)
        {
            ForwardSession session = new ForwardSession(incoming, targetPort, RemoveSession);
            bool reject = false;
            Exception startupError = null;
            lock (gate)
            {
                if (Volatile.Read(ref stopping) != 0)
                {
                    reject = true;
                }
                else
                {
                    sessions.Add(session);
                    try { session.Start(); }
                    catch (Exception ex)
                    {
                        sessions.Remove(session);
                        startupError = ex;
                    }
                }
            }
            if (reject) { session.StopAndJoin(); return; }
            if (startupError != null)
            {
                session.StopAndJoin();
                throw startupError;
            }
        }

        private void RemoveSession(ForwardSession session)
        {
            lock (gate) { sessions.Remove(session); }
        }

        private void Stop()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0) return;
            try { listener.Stop(); } catch { }

            List<ForwardSession> active;
            lock (gate)
            {
                active = new List<ForwardSession>(sessions);
                sessions.Clear();
            }
            for (int i = 0; i < active.Count; i++) active[i].StopAndJoin();

            if (readyCreated)
            {
                try
                {
                    if (File.Exists(readyPath) && string.Equals(File.ReadAllText(readyPath), readyContents, StringComparison.Ordinal))
                        File.Delete(readyPath);
                }
                catch (Exception ex) { Console.Error.WriteLine("Could not remove owned readyFile: " + ex.Message); }
                readyCreated = false;
            }
        }

        public void Dispose() { Stop(); }
    }

    private sealed class ForwardSession
    {
        private readonly TcpClient incoming;
        private readonly int targetPort;
        private readonly Action<ForwardSession> onFinished;
        private readonly object gate = new object();
        private TcpClient outgoing;
        private Thread worker;
        private bool stopped;
        private int closed;

        public ForwardSession(TcpClient incoming, int targetPort, Action<ForwardSession> onFinished)
        {
            this.incoming = incoming;
            this.targetPort = targetPort;
            this.onFinished = onFinished;
        }

        public void Start()
        {
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "LobbyTcpForwarder.Session";
            worker.Start();
        }

        private void Run()
        {
            try
            {
                TcpClient target = new TcpClient(AddressFamily.InterNetwork);
                lock (gate)
                {
                    if (stopped)
                    {
                        target.Close();
                        return;
                    }
                    outgoing = target;
                }

                target.Connect(IPAddress.Loopback, targetPort);
                if (IsStopped()) return;
                incoming.NoDelay = true;
                target.NoDelay = true;

                NetworkStream fromClient = incoming.GetStream();
                NetworkStream fromTarget = target.GetStream();
                Thread clientToTarget = StartPump(fromClient, target, "ClientToTarget");
                Thread targetToClient = StartPump(fromTarget, incoming, "TargetToClient");
                clientToTarget.Join();
                targetToClient.Join();
            }
            catch (Exception ex)
            {
                if (!IsStopped()) Console.Error.WriteLine("Forward session ended: " + ex.Message);
            }
            finally
            {
                Close();
                onFinished(this);
            }
        }

        private static Thread StartPump(NetworkStream source, TcpClient destination, string suffix)
        {
            Thread thread = new Thread(new ThreadStart(delegate { Pump(source, destination); }));
            thread.IsBackground = true;
            thread.Name = "LobbyTcpForwarder." + suffix;
            thread.Start();
            return thread;
        }

        private static void Pump(NetworkStream source, TcpClient destination)
        {
            byte[] buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    int count = source.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    NetworkStream output = destination.GetStream();
                    output.Write(buffer, 0, count);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                try { destination.Client.Shutdown(SocketShutdown.Send); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private bool IsStopped()
        {
            lock (gate) { return stopped; }
        }

        public void StopAndJoin()
        {
            Close();
            Thread current;
            lock (gate) { current = worker; }
            if (current != null && current != Thread.CurrentThread)
            {
                try { current.Join(2000); } catch (ThreadStateException) { }
            }
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            TcpClient target;
            lock (gate)
            {
                stopped = true;
                target = outgoing;
            }
            try { incoming.Close(); } catch { }
            if (target != null) { try { target.Close(); } catch { } }
        }
    }
}

