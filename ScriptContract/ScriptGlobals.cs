namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Global object container.
    /// Scripts can access 'StateStore' directly via this static class.
    /// </summary>
    /// Scripts can access 'State', 'Log', 'Client', 'Proxy' directly via this static class.
    /// </summary>
    public static class ScriptGlobals
    {
        // Global Singletons (Injected)
        public static IScriptStateStore State { get; set; } = null!;
        public static IScriptLogger Log { get; set; } = null!;

        // Mode Specific APIs
        public static IClientApi? Client { get; private set; }
        public static IProxyApi? Proxy { get; private set; }
        
        // System Services
        public static IPacketRegistry Registry { get; set; } = null!;
        public static IPacketCodec Codec { get; set; } = null!;
        
        public static void Initialize(IScriptStateStore state, IScriptLogger log)
        {
            State = state;
            Log = log;
        }

        public static void SetApis(IClientApi? client, IProxyApi? proxy)
        {
            Client = client;
            Proxy = proxy;
        }

        public static void SetServices(IPacketRegistry registry, IPacketCodec codec)
        {
            Registry = registry;
            Codec = codec;
        }
    }
}
