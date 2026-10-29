using System;
using System.IO;
using IceyCoop.Transport;

namespace IceyCoop.RelayHost
{
    /// <summary>
    /// T23: the runnable relay node. Both players point at it with
    /// ICEYCOOP_RELAY=ROOMCODE@this-host:port and it copies bytes between them, so neither player
    /// needs an open port of their own.
    ///
    /// Console protocol (one line, flushed, machine readable - the test runner reads it):
    ///   RELAY_LISTENING port=N      printed once the socket is bound
    ///   RELAY_PAIRED sessions=N     printed whenever the paired-session count changes
    ///
    /// Stops when the -StopFile appears (the runner's shutdown signal) or on EOF/Ctrl+C.
    /// </summary>
    public static class RelayHost
    {
        public static int Main(string[] args)
        {
            int port = RelayTarget.DefaultPort;
            string stopFile = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-Port", StringComparison.OrdinalIgnoreCase))
                {
                    int parsed;
                    if (!int.TryParse(args[i + 1], out parsed) || parsed < 0 || parsed > 65535)
                    {
                        Console.Error.WriteLine("RELAY_ERROR bad port: " + args[i + 1]);
                        return 2;
                    }
                    port = parsed;
                }
                else if (string.Equals(args[i], "-StopFile", StringComparison.OrdinalIgnoreCase))
                {
                    stopFile = args[i + 1];
                }
            }

            RelayServer server = new RelayServer(new RoomRegistry());
            try
            {
                server.Start(port);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("RELAY_ERROR start failed: " + ex.Message);
                return 1;
            }
            Console.WriteLine("RELAY_LISTENING port=" + server.Port);
            Console.Out.Flush();

            int lastPaired = 0;
            int statusTick = 0;
            try
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(200);
                    int paired = server.PairedSessions;
                    if (paired != lastPaired)
                    {
                        lastPaired = paired;
                        Console.WriteLine("RELAY_PAIRED sessions=" + paired);
                        Console.Out.Flush();
                    }
                    if (++statusTick % 10 == 0)
                    {
                        Console.WriteLine("RELAY_STATUS sessions=" + paired + " first_to_second=" + server.BytesFromFirstPeer
                            + " second_to_first=" + server.BytesFromSecondPeer);
                        Console.Out.Flush();
                    }
                    if (!string.IsNullOrEmpty(stopFile) && File.Exists(stopFile))
                    {
                        Console.WriteLine("RELAY_STOPPING reason=stop-file");
                        Console.Out.Flush();
                        break;
                    }
                }
            }
            finally
            {
                server.Stop();
                server.Dispose();
            }
            return 0;
        }
    }
}
