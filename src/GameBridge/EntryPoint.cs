using System;
using IceyCoop.Bridge;

namespace IceyCoop.GameBridge
{
    public static class EntryPoint
    {
        public static ICoopBridge CreateBridge(BootOptions options)
        {
            return new ProbeBridge();
        }
    }
}
