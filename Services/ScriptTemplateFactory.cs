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
                "PacketInterceptor" => GetInterceptorTemplate(),
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

        public static string GetInterceptorTemplate() =>
@"using System;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

// [Optional] Unified Packet Interception Logic
// Works for Manual Send, Proxy, and Replay (Server <-> Client)
public class MyInterceptor : IPacketInterceptor
{
    // [Manual Send / Proxy Request / Replay Request]
    // Called when a packet is going OUT to the Server.
    public ValueTask OnOutboundAsync(PacketContext context)
    {
        // Example: Inspect or Modify
        // if (context.Packet is LoginReq req) { ... }
        return ValueTask.CompletedTask;
    }

    // [Proxy Response / Replay Response]
    // Called when a packet is coming IN from the Server.
    public ValueTask OnInboundAsync(PacketContext context)
    {
        // Example: Log or Drop
        // context.Drop = true; 
        return ValueTask.CompletedTask;
    }
}";
    }
}
