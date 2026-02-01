using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using System;

namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Unified Context for Packet Interception.
    /// Used for both Client (Send) and Proxy (Inbound/Outbound) scenarios.
    /// </summary>
    public sealed class PacketContext
    {
        /// <summary>
        /// The decoded message object (if available).
        /// </summary>
        public Packet? Packet { get; set; }

        /// <summary>
        /// The raw message object (for Client Send scenario).
        /// </summary>
        public IMessage? Message { get; set; }

        /// <summary>
        /// Key-Value metadata/headers.
        /// Replaces ClientPacketContext.Headers.
        /// </summary>
        public Dictionary<string, string> Metadata { get; } = new Dictionary<string, string>();

        /// <summary>
        /// The direction of the packet flow.
        /// </summary>
        public PacketDirection Direction { get; }

        /// <summary>
        /// If set to true, the packet is dropped (Proxy only).
        /// </summary>
        public bool Drop { get; set; }

        /// <summary>
        /// If set to true, forwards Raw bytes without re-serialization (Proxy only).
        /// </summary>
        public bool Bypass { get; set; }
        
        /// <summary>
        /// The original raw bytes (Proxy only).
        /// </summary>
        public ReadOnlyMemory<byte> Raw { get; }

        // Constructor for Proxy
        public PacketContext(Packet packet, PacketDirection direction, ReadOnlyMemory<byte> raw)
        {
            Packet = packet;
            Direction = direction;
            Raw = raw;
        }

        // Constructor for Client Send
        public PacketContext(IMessage message)
        {
            Message = message;
            Direction = PacketDirection.Outbound;
        }
    }
}
