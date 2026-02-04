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

// [Optional] Custom Packet Registry Implementation
public class PacketRegistry : IPacketRegistry
{
    private readonly Dictionary<int, Type> _idToType = new();
    private readonly Dictionary<Type, int> _typeToId = new();
    
    // Automatically populated system types
    private IReadOnlyCollection<Type> _types = [];

    // Return registered types
    public IEnumerable<Type> GetMessageTypes() => _types;
    public IReadOnlyList<Type> GetMessageTypesRequest() => _types;

    public Type? GetMessageType(int msgId) => _idToType.GetValueOrDefault(msgId);

    public int GetMsgId(Type type)
    {
        if (_typeToId.TryGetValue(type, out var id))
            return id;
            
        throw new KeyNotFoundException($""MsgId not found for type: {type.Name}"");
    }

    // Called automatically with all loaded Proto types
    public void RegisterMessageType(IReadOnlyList<Type> types) 
    {
        _types = types.ToArray();

        // -----------------------------------------------------------------
        // TODO: Map ID to Type here.
        // You can use _types to iterate or manually register.
        // -----------------------------------------------------------------
        
        // Example: Manual
        // Register(1001, typeof(MyGame.LoginReq));
        
        // Example: Auto (by name/attribute/etc)
        // foreach (var t in types) { ... }
    }
    
    private void Register(int id, Type type)
    { 
        _idToType[id] = type;
        _typeToId[type] = id;
    }

    public MessageParser GetParserById(int msgId)
    {
        if (_idToType.TryGetValue(msgId, out var type))
        {
            var prop = type.GetProperty(""Parser"", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop?.GetValue(null) is MessageParser parser)
                return parser;
        }
        throw new NotImplementedException($""Parser not found for MsgId: {msgId}"");
    }
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