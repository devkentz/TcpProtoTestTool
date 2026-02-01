using System.Threading.Tasks;

namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Unified Interceptor Interface.
    /// Implement this to intercept packets in Client, Proxy, or Replay modes.
    /// </summary>
    public interface IPacketInterceptor
    {
        /// <summary>
        /// Called when a packet is received from the Server (Server -> Proxy -> Client).
        /// </summary>
        ValueTask OnInboundAsync(PacketContext context);

        /// <summary>
        /// Called when a packet is sent to the Server (Client -> Proxy -> Server) OR (TestClient -> Server).
        /// </summary>
        ValueTask OnOutboundAsync(PacketContext context);
    }
}
