namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// API for Reverse Proxy Mode operations.
    /// Allows controlling the proxy behavior globally or inspecting state.
    /// </summary>
    public interface IProxyApi
    {
        /// <summary>
        /// Gets the direction of the current packet being processed.
        /// </summary>
        PacketDirection Direction { get; }

        /// <summary>
        /// drops the current packet (if supported in the current context).
        /// </summary>
        void Drop();

        // Future: Inject, Rewrite(byte[]) etc. can be added here if not handled by Context.
    }
}
