using System;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

// ************************************************************
// * Packet Handler Script
// * 
// * Implement Interceptors to intercept/modify traffic.
// ************************************************************

public class MyPacketHandler : IProxyPacketInterceptor, IClientPacketInterceptor
{
    // Called when Proxy receives data from Client (Upstream)
    public ValueTask OnInboundAsync(ProxyPacketContext context)
    {
        // context.Raw is the byte array.
        // You can decode it here if needed or just log size.
        ScriptGlobals.Log.Info($"[Proxy] Inbound: {context.Raw.Length} bytes");
        
        return ValueTask.CompletedTask;
    }

    // Called when Proxy receives data from Server (Downstream)
    public ValueTask OnOutboundAsync(ProxyPacketContext context)
    {
        ScriptGlobals.Log.Info($"[Proxy] Outbound: {context.Raw.Length} bytes");
        return ValueTask.CompletedTask;
    }

    // Called before Test Client sends a packet
    public void OnBeforeSend(ClientPacketContext context)
    {
        ScriptGlobals.Log.Info($"[Client] Sending Packet: {context.Message}");
    }
}