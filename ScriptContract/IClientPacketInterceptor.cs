namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Interceptor for Test Client Mode.
    /// Executed BEFORE serialization and sending.
    /// </summary>
    public interface IClientPacketInterceptor
    {
        /// <summary>
        /// Called before the packet is serialized and sent to the server.
        /// </summary>
        void OnBeforeSend(ClientPacketContext context);
    }
}
