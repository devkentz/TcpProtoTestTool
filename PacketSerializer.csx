
// #load "C:\Work\backup\tcpaop\Tests\ProtoTestTool\bin\Debug\net9.0-windows\PacketHeader.csx"
// NOTE: PacketHeader.csx is automatically referenced by the compilation pipeline.
// using #load causes "Same Type, Different Assembly" conflicts.

#r "nuget: K4os.Compression.LZ4, 1.3.8"
#r "nuget: Microsoft.IO.RecyclableMemoryStream, 3.0.1"
#r "nuget: Google.Protobuf,3.28.2"
#r "nuget: System.Memory, 4.5.5"
#load "PacketHeader.csx"

using System;
using System.IO;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using ProtoTestTool.ScriptContract;
using K4os.Compression.LZ4;
using Microsoft.IO;
using Google.Protobuf;
using System.Diagnostics.CodeAnalysis;

IPacketCodec
public static class PacketDefine
{
    public static int MaxPacketSize { get; set; } = 1024 * 1024 * 2;
}

public static class ProtobufCompressor
{
    // 최대 버퍼 크기 제한 (안전 장치)
    private const int MaxBufferSize = 1024 * 1024 * 2;
    private static readonly RecyclableMemoryStreamManager StreamManager = new();

    public static int Compress(IMessage message, Span<byte> output)
    {
        // 1. 직렬화
        // using을 사용하여 반드시 반환되도록 함
        using var stream = StreamManager.GetStream();

        // 중요: 스트림에 직접 써야 Position이 업데이트 됩니다.
        message.WriteTo((Stream) stream);

        int serializedSize = (int) stream.Length;

        // 2. 버퍼 가져오기 (GetBuffer는 내부 배열을 반환하며, Length보다 클 수 있음)
        ReadOnlySpan<byte> serialized = stream.GetBuffer().AsSpan(0, serializedSize);

        // 3. 압축 공간 확인
        int maxSize = LZ4Codec.MaximumOutputSize(serializedSize);
        if (output.Length < maxSize)
        {
            // output 버퍼가 너무 작으면 예외 처리 혹은 false 반환
            // 여기서는 원본 의도대로 예외 발생
            throw new ArgumentException($"Output buffer too small. Needed: {maxSize}, Got: {output.Length}");
        }

        // 4. 압축 (반환값은 실제 압축된 바이트 수)
        return LZ4Codec.Encode(serialized, output);
    }

    public static IMessage DecompressMessage(MessageParser parser, ReadOnlySpan<byte> compressed, int originalSize)
    {
        if (originalSize > MaxBufferSize)
            throw new InvalidOperationException($"Message size {originalSize} exceeds MaxBufferSize {MaxBufferSize}");

        // 1. 스트림 가져오기 (using 필수!)
        using var stream = StreamManager.GetStream();

        // 2. 버퍼 확보
        stream.SetLength(originalSize);

        // GetBuffer()를 통해 내부 배열에 접근
        var decompressBuffer = stream.GetBuffer();

        // 3. 압축 해제
        int decodedSize = LZ4Codec.Decode(compressed, decompressBuffer.AsSpan(0, originalSize));

        if (decodedSize != originalSize)
        {
            throw new InvalidOperationException(
                $"Decompressed size {decodedSize} != expected {originalSize}"
            );
        }

        return parser.ParseFrom(decompressBuffer.AsSpan()[.. decodedSize]);
    }
}


public static class PacketUtil
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalcSize(Header header, IMessage message)
    {
        return header.GetSize() + message.CalculateSize();
    }

    public static byte[] WriteToSpan(
        Header header,
        IMessage message,
        int packetSize = 0)
    {
    
        packetSize = packetSize > 0 ? packetSize : CalcSize(header, message);

        var buffer = new byte[packetSize];

        if (packetSize > PacketDefine.MaxPacketSize)
            throw new Exception($"packet size is over : {packetSize}");

        int offset = 0;

        // packet size
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), packetSize);
        offset += 4;
        
        // flags
        buffer[offset++] = (byte)header.Flags;

        // msgId
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), header.MsgId);
        offset += 4;

        // msgSeq
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), header.MsgSeq);
        offset += 2;

        if (header.HasError)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), header.ErrorCode);
            offset += 2;
        }

        int payloadSize = packetSize - header.GetSize();

        if (header.IsCompressed)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), payloadSize);
            offset += 4;

            ProtobufCompressor.Compress(message, buffer.AsSpan(offset, payloadSize));
            offset += payloadSize;
        }
        else
        {
            message.WriteTo(buffer.AsSpan(offset, payloadSize));
            offset += payloadSize;
        }

        return buffer;
    }
}


public class PacketCodec : IPacketCodec
{
    // Default: 4-byte Length + Payload
    // Format: [Length(4)][MsgId(4)][Payload...]
    
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, [NotNullWhen(true)] out Packet? message)
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
        //message = new { RawSize = len, Data = payloadSeq.ToArray() };
        
        buffer = buffer.Slice(4 + len);
        return true;
    }

    public ReadOnlyMemory<byte> Encode(Packet packet)
    {
        return PacketUtil.WriteToSpan((Header)packet.Header, packet.Message);
    }
}
