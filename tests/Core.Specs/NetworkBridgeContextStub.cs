namespace IceyCoop.GameBridge
{
    // The pure network components use the bridge solely for diagnostic logging.
    public static class BridgeContext
    {
        public static void Info(string message) { }
        public static void Error(string message) { }
    }
}
