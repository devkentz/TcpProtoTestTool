using System;
using System.Buffers;
using ProtoTestTool.ScriptContract;

public class PacketCodec : IPacketCodec
{
    // Default: 4-byte Length + Payload
    // Format: [Length(4)][MsgId(4)][Payload...]
    
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out object? message)
    {
        message = null;
        if (buffer.Length < 4) return false;

        var set = buffer.Slice(0, 4);
        Span<byte> header = stackalloc byte[4];
        set.CopyTo(header);
        var len = BitConverter.ToInt32(header);

        if (buffer.Length < 4 + len) return false;

        // Check Registry for MsgId (Assumes [Len][MsgId][Protobuf])
        var payloadSeq = buffer.Slice(4, len);
        
        // Example: Peeking MsgId at offset 4 (after length)
        if (payloadSeq.Length >= 4)
        {
             var msgIdSeq = payloadSeq.Slice(0, 4);
             Span<byte> msgIdBytes = stackalloc byte[4];
             msgIdSeq.CopyTo(msgIdBytes);
             int msgId = BitConverter.ToInt32(msgIdBytes);
             
             // TODO: Use a Registry if available or Reflection lookup
             // System.Console.WriteLine($"MsgId: {msgId}");
        }

        // Return raw wrapper for now or implement ProtoParser call
        message = new { RawSize = len, Data = payloadSeq.ToArray() };

        buffer = buffer.Slice(4 + len);
        return true;
    }

    public ReadOnlyMemory<byte> Encode(object message)
    {
        // Placeholder
        return new byte[4]; 
    }
}