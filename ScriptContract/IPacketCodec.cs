using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;

namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Responsible for converting between TCP byte streams and Message objects.
    /// This is the SINGLE point of truth for serialization/deserialization.
    /// </summary>
    public interface IPacketCodec
    {
        /// <summary>
        /// Tries to decode a message from the buffer.
        /// </summary>
        /// <param name="buffer">The incoming sequence of bytes.</param>
        /// <param name="message">The decoded message object (e.g. Google.Protobuf.IMessage).</param>
        /// <returns>True if a message was successfully decoded and consumed from the buffer.</returns>
        int TryDecode(ref ReadOnlySpan<byte> buffer, out Packet? message);

        /// <summary>
        /// Encodes a message object into a byte array (including framing/length-prefix).
        /// </summary>
        /// <param name="packet"></param>
        /// <returns>The raw bytes to send over the wire.</returns>
        //ReadOnlyMemory<byte> Encode(IMessage message);
        ReadOnlyMemory<byte> Encode(Packet packet);
    }
}