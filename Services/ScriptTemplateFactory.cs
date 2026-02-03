using System;

namespace ProtoTestTool.Services
{
    public static class ScriptTemplateFactory
    {
        public static string GetTemplate(string featureName, string className = "MyInterceptor")
        {
            return featureName switch
            {
                "PacketHeader" => GetPacketHeaderTemplate(),
                "PacketCodec" => GetPacketCodecTemplate(),
                "PacketRegistry" => GetPacketRegistryTemplate(),
                "PacketInterceptor" => GetInterceptorTemplate(className),
                _ => "// Not found"
            };
        }

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

        public static string GetPacketRegistryTemplate() =>
@"using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using ProtoTestTool.ScriptContract;

// [Optional] Custom Packet Registry
// Implement this to manually register packet types with IDs.
// If not provided, the tool uses the default proto-based registry.
public class PacketRegistry : IPacketRegistry
{
    private readonly Dictionary<int, Type> _idToType = new();
    private readonly Dictionary<Type, int> _typeToId = new();
    private readonly Dictionary<int, MessageParser> _parsers = new();

    public IEnumerable<Type> GetMessageTypes() => _idToType.Values;
    public Type? GetMessageType(int msgId) => _idToType.GetValueOrDefault(msgId);
    public int GetMsgId(Type type) => _typeToId.GetValueOrDefault(type);
    public MessageParser GetParserById(int msgId) => _parsers[msgId];
}";

        public static string GetInterceptorTemplate(string className) =>
$@"using System;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

// [Optional] Unified Packet Interception Logic
// Works for Manual Send, Proxy, and Replay (Server <-> Client)
public class {className} : IPacketInterceptor
{{
    // [Manual Send / Proxy Request / Replay Request]
    // Called when a packet is going OUT to the Server.
    public ValueTask OnOutboundAsync(PacketContext context)
    {{
        // Example: Inspect or Modify
        // if (context.Packet is LoginReq req) {{ ... }}
        return ValueTask.CompletedTask;
    }}

    // [Proxy Response / Replay Response]
    // Called when a packet is coming IN from the Server.
    public ValueTask OnInboundAsync(PacketContext context)
    {{
        // Example: Log or Drop
        // context.Drop = true; 
        return ValueTask.CompletedTask;
    }}
}}";
    }
}
