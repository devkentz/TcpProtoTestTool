using System;
using System.IO;
using System.Threading.Tasks;

namespace ProtoTestTool.Services
{
    public class ScaffoldingService
    {
        public async Task InitializeWorkspaceAsync(string workspacePath)
        {
            if (!Directory.Exists(workspacePath))
            {
                Directory.CreateDirectory(workspacePath);
            }

            var scriptsDir = Path.Combine(workspacePath, "Scripts");
            var protosDir = Path.Combine(workspacePath, "Protos");
            var libsDir = Path.Combine(scriptsDir, "Libs");

            Directory.CreateDirectory(scriptsDir);
            Directory.CreateDirectory(protosDir);
            Directory.CreateDirectory(libsDir);

            await CreateFileIfNotExists(Path.Combine(protosDir, "readme.txt"), 
                "Place your .proto files in this directory.\nThey will be automatically compiled and loaded.");

            // Create Default Script Templates
            await CreateFileIfNotExists(Path.Combine(scriptsDir, "PacketInterceptor.cs"), GetPacketInterceptorTemplate());
            await CreateFileIfNotExists(Path.Combine(scriptsDir, "PacketCodec.cs"), GetPacketCodecTemplate());
            await CreateFileIfNotExists(Path.Combine(scriptsDir, "PacketRegistry.cs"), GetPacketRegistryTemplate());
            await CreateFileIfNotExists(Path.Combine(scriptsDir, "PacketHeader.cs"), GetPacketHeaderTemplate());
        }

        private async Task CreateFileIfNotExists(string path, string content)
        {
            if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(path, content);
            }
        }

        private string GetPacketInterceptorTemplate() =>
@"using System;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

public class Interceptor : IProxyPacketInterceptor
{
    public ValueTask OnInboundAsync(ProxyPacketContext context)
    {
        throw new NotImplementedException();
    }

    public ValueTask OnOutboundAsync(ProxyPacketContext context)
    {
        throw new NotImplementedException();
    }
}";

        private string GetPacketCodecTemplate() =>
@"using System;
using System.Buffers;
using ProtoTestTool.ScriptContract;

public class PacketCodec : IPacketCodec
{
    public int TryDecode(ref ReadOnlySpan<byte> span, out Packet? packet)
    {
        throw new NotImplementedException();
    }

    public ReadOnlyMemory<byte> Encode(Packet packet)
    {
        throw new NotImplementedException();
    }
}";

        private string GetPacketHeaderTemplate() =>
@"using System;
using ProtoTestTool.ScriptContract;

public class Header : IHeader
{
    public string ToJsonString()
    {
        throw new NotImplementedException();
    }
}";

        private string GetPacketRegistryTemplate() =>
@"using System;
using System.Collections.Generic;
using ProtoTestTool.ScriptContract;
using Google.Protobuf;

public class PacketRegistry : IPacketRegistry
{
    private readonly Dictionary<int, Type> _idToType = new Dictionary<int, Type>();
    private readonly Dictionary<Type, int> _typeToId = new Dictionary<Type, int>();

    public void Register(int msgId, Type type, string? msgName = null, bool? isRequest = null)
    {
        _idToType[msgId] = type;
        _typeToId[type] = msgId;
    }

    public IEnumerable<Type> GetMessageTypes() => _idToType.Values;

    public Type? GetMessageType(int msgId) => _idToType.TryGetValue(msgId, out var type) ? type : null;

    public int GetMsgId(Type type) => _typeToId.TryGetValue(type, out var id) ? id : 0;

    public MessageParser GetParserById(int msgId)
    {
        throw new NotImplementedException();
    }
}";

    }
}
