using System;
using ProtoTestTool.ScriptContract;
using System.Threading.Tasks;

// ****************************************
// *      USER IMPLEMENTATION AREA        *
// ****************************************

public class MyInterceptor : IProxyPacketInterceptor
{
    public ValueTask OnInboundAsync(ProxyPacketContext context)
    {
        // Example: Count packets using State
        if (ScriptGlobals.State.TryGet<int>("PacketCount", out var count))
        {
            ScriptGlobals.State.Set("PacketCount", count + 1);
        }
        else
        {
            ScriptGlobals.State.Set("PacketCount", 1);
        }

        ScriptGlobals.Log.Info($"[Script] Packet Received. Count: {ScriptGlobals.State.Get<int>("PacketCount")}");
        
        return ValueTask.CompletedTask;
    }

    public ValueTask OnOutboundAsync(ProxyPacketContext context)
    {
        return ValueTask.CompletedTask;
    }
}