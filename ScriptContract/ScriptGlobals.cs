namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Global object container.
    /// Scripts can access 'State', 'Log', 'Client', 'Proxy' directly via this static class.
    /// </summary>
    public static class ScriptGlobals
    {
        private static volatile IScriptStateStore _state = null!;
        private static volatile IScriptLogger _log = null!;
        private static volatile IPacketRegistry _registry = null!;
        private static volatile IPacketCodec _codec = null!;

        public static IScriptStateStore State
        {
            get => _state;
            set => _state = value;
        }

        public static IScriptLogger Log
        {
            get => _log;
            set => _log = value;
        }

        public static IPacketRegistry Registry
        {
            get => _registry;
            set => _registry = value;
        }

        public static IPacketCodec Codec
        {
            get => _codec;
            set => _codec = value;
        }

        public static void Initialize(IScriptStateStore state, IScriptLogger log)
        {
            State = state;
            Log = log;
        }

        public static void SetServices(IPacketRegistry registry, IPacketCodec codec)
        {
            Registry = registry;
            Codec = codec;
        }
    }
}
