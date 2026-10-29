using System;
using System.IO;
using System.Reflection;
using IceyCoop.Bridge;

namespace IceyCoop.Bootstrap
{
    /// <summary>
    /// Loads IceyCoop.GameBridge.dll from BepInEx/icey-coop/lib after the
    /// chainloader has finished, so GameBridge may reference game types later.
    /// </summary>
    internal static class PrivateAssemblyLoader
    {
        private const string LibAssembly = "IceyCoop.GameBridge.dll";
        private const string EntryType = "IceyCoop.GameBridge.EntryPoint";
        private const string EntryMethod = "CreateBridge";

        public static ICoopBridge LoadBridge(BootOptions options)
        {
            string bepRoot = BepInExRoot();
            if (string.IsNullOrEmpty(bepRoot))
            {
                throw new InvalidOperationException("BepInEx root not resolved");
            }

            string lib = Path.Combine(Path.Combine(bepRoot, "icey-coop"), "lib");
            string dll = Path.Combine(lib, LibAssembly);
            if (!File.Exists(dll))
            {
                throw new FileNotFoundException("GameBridge dll missing", dll);
            }

            Assembly asm = Assembly.LoadFrom(dll);
            Type t = asm.GetType(EntryType, true);
            MethodInfo m = t.GetMethod(EntryMethod, BindingFlags.Public | BindingFlags.Static);
            if (m == null)
            {
                throw new MissingMethodException(EntryType + "." + EntryMethod);
            }

            object result = m.Invoke(null, new object[] { options });
            ICoopBridge bridge = result as ICoopBridge;
            if (bridge == null)
            {
                throw new InvalidCastException("bridge does not implement ICoopBridge");
            }
            return bridge;
        }

        private static string BepInExRoot()
        {
            string loc = typeof(PrivateAssemblyLoader).Assembly.Location;
            string plugins = Path.GetDirectoryName(loc);
            return Path.GetDirectoryName(plugins);
        }
    }
}
