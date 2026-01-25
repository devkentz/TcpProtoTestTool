using System;
using System.IO;
using Google.Protobuf;
using ProtoTestTool.ScriptContract;

public class PacketSerializer : IPacketSerializer
{
    private readonly IPacketRegistry _registry;
    private readonly Action<string> _logger;

    public PacketSerializer(IPacketRegistry registry, Action<string> logger)
    {
        _registry = registry;
        _logger = logger ?? Console.WriteLine;
    }

    public int GetHeaderSize() => 4;

    public int GetTotalLength(byte[] headerBuffer)
    {
        if (headerBuffer.Length < 4) return 0;
        return BitConverter.ToInt32(headerBuffer, 0);
    }

    public byte[] Serialize(object packet)
    {
        // _logger($"[Serializer] Serializing {packet.GetType().Name}...");

        if (packet is not IMessage message)
            throw new ArgumentException("Packet must implement IMessage");

        var msgId = _registry.GetMessageId(packet.GetType());
        if (msgId == 0)
            throw new Exception($"Message ID not found for type {packet.GetType().Name}");

        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write((int)0); // Placeholder Size
            writer.Write((byte)0); // Flags
            writer.Write(msgId);   // MsgId
            writer.Write((ushort)0); // MsgSeq

            message.WriteTo(ms);

            var totalSize = (int)ms.Length;
            ms.Position = 0;
            writer.Write(totalSize);

            return ms.ToArray();
        }
    }

    public object Deserialize(byte[] buffer)
    {
        using (var ms = new MemoryStream(buffer))
        using (var reader = new BinaryReader(ms))
        {
            var totalSize = reader.ReadInt32();
            var flags = reader.ReadByte();
            var msgId = reader.ReadInt32();
            var msgSeq = reader.ReadUInt16();

            // _logger($"[Serializer] Deserialize Header - Id:{msgId}, Size:{totalSize}");

            if ((flags & 0x40) != 0) 
            {
                var errorCode = reader.ReadUInt16();
                throw new Exception($"Packet Error: {errorCode}");
            }

            var type = _registry.GetMessageType(msgId);
            if (type == null)
                throw new Exception($"Unknown Message ID: {msgId}");

            var parserProp = type.GetProperty("Parser", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (parserProp == null)
                throw new Exception($"Parser property not found on {type.Name}");

            var parser = (MessageParser)parserProp.GetValue(null);
            
            var payloadSize = totalSize - (int)ms.Position;
            var payload = reader.ReadBytes(payloadSize);

            return parser.ParseFrom(payload);
        }
    }
}