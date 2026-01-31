using System;

namespace ProtoTestTool.Services
{
    public static class ScriptTemplateFactory
    {
        public static string GetTemplate(string featureName)
        {
            return featureName switch
            {
                "PacketLoader" => GetPacketLoaderTemplate(),
                "PacketHeader" => GetPacketHeaderTemplate(),
                "PacketCodec" => GetPacketCodecTemplate(),
                "PacketInterceptor_Proxy" => GetProxyInterceptorTemplate(),
                "PacketInterceptor_Client" => GetClientInterceptorTemplate(),
                "PacketInterceptor_Replay" => GetReplayInterceptorTemplate(),
                "PacketInterceptor" => GetProxyInterceptorTemplate(), // Default
                _ => "// Not found"
            };
        }

        public static string GetPacketLoaderTemplate() =>
@"using System;
using System.Collections.Generic;
using System.Linq;
using ProtoTestTool.ScriptContract;
using Google.Protobuf;

// [Mandatory] Packet Registration Logic
public class PacketLoader : IPacketLoader
{
    // [Mandatory] Implement this method to register your packet types
    public void Load(IPacketRegistry registry)
    {
        // Strategy 1: Manual Registration
        // registry.Register(1001, typeof(LoginReq), isRequest: true);
        
        // Strategy 2: Bulk Registration by Convention
        // Implement your own logic to determine ID and Direction
        /*
        foreach (var type in registry.GetMessageTypes())
        {
            if (type.Name.EndsWith(""Req""))
            {
                int id = GetIdFromType(type);
                registry.Register(id, type, isRequest: true);
            }
            else if (type.Name.EndsWith(""Res""))
            {
                int id = GetIdFromType(type);
                registry.Register(id, type, isRequest: false);
            }
        }
        */
    }

    private int GetIdFromType(Type type)
    {
        // Example: return (int)type.GetField(""MsgId"").GetValue(null);
        return 0;
    }
}";

        public static string GetPacketHeaderTemplate() =>
@"using System;
using ProtoTestTool.ScriptContract;

// [Mandatory] Packet Header Definition
public class Header : IHeader
{
    // [Mandatory] Return a JSON string representing the header structure
    public string ToJsonString()
    {
        // Example: return ""{ \""msgId\"": 0 }"";
        throw new NotImplementedException();
    }
}";

        public static string GetPacketCodecTemplate() =>
@"using System;
using System.Buffers;
using ProtoTestTool.ScriptContract;

// [Mandatory] Packet Encoding/Decoding Logic
public class PacketCodec : IPacketCodec
{
    // [Mandatory] Decode raw bytes into a Packet object (Header + Body)
    public int TryDecode(ref ReadOnlySpan<byte> span, out Packet? packet)
    {
        // Implement your retrieval logic here
        throw new NotImplementedException();
    }

    // [Mandatory] Encode a Packet object into raw bytes
    public ReadOnlyMemory<byte> Encode(Packet packet)
    {
        // Implement your encoding logic here
        throw new NotImplementedException();
    }
}";

        public static string GetProxyInterceptorTemplate() =>
@"using System;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

// [Optional] Proxy Packet Interception Logic
public class ProxyInterceptor : IProxyPacketInterceptor
{
    // [Optional] Called before sending a packet to the server (Upstream)
    public ValueTask OnInboundAsync(ProxyPacketContext context)
    {
        // Example: Modify context.Packet before forwarding to server
        return ValueTask.CompletedTask;
    }

    // [Optional] Called before sending a packet to the client (Downstream)
    public ValueTask OnOutboundAsync(ProxyPacketContext context)
    {
        // Example: Log or modify context.Packet before forwarding to client
        return ValueTask.CompletedTask;
    }
}";

        public static string GetClientInterceptorTemplate() =>
@"using System;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

// [Optional] Client Packet Interception Logic
// Intercepts packets sent/received by the Test Client features.
public class ClientInterceptor : IClientPacketInterceptor
{
    public void OnBeforeSend(ClientPacketContext context)
    {
        // Called before sending to server
        // Example: context.Message = new LoginReq();
    }
}";

        public static string GetReplayInterceptorTemplate() =>
@"using System;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

// [Optional] Replay Packet Interception Logic
// Used during Packet Replay mode to simulate state or modify replayed packets.
// Note: Uses the same interface as Proxy, but context might differ.
public class ReplayInterceptor : IProxyPacketInterceptor
{
    public ValueTask OnInboundAsync(ProxyPacketContext context)
    {
        // Handle Request Replay
        return ValueTask.CompletedTask;
    }

    public ValueTask OnOutboundAsync(ProxyPacketContext context)
    {
        // Handle Response Replay
        return ValueTask.CompletedTask;
    }
}";
    }
}
