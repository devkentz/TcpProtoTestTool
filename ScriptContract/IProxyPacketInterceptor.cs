using System.Threading.Tasks;

namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Interceptor for Reverse Proxy Mode.
    /// Executed AFTER decoding and BEFORE re-serialization.
    /// </summary>
    public interface IProxyPacketInterceptor
    {
        /// <summary>
        /// Called when a packet is received from the Client (heading to Server).
        /// </summary>
        ValueTask OnInboundAsync(ProxyPacketContext context);

        /// <summary>
        /// Called when a packet is received from the Server (heading to Client).
        /// </summary>
        ValueTask OnOutboundAsync(ProxyPacketContext context);
    }
}
