using System.Threading.Tasks;
using Google.Protobuf;

namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Context for Reverse Proxy Mode interceptors.
    /// Allows inspection, modification, dropping, and bypassing of packets.
    /// </summary>
    public sealed class ProxyPacketContext
    {
        /// <summary>
        /// The decoded message object.
        /// </summary>
        public Packet Packet { get; set; }

        /// <summary>
        /// The direction of the packet flow (Inbound = Client->Server, Outbound = Server->Client).
        /// </summary>
        public PacketDirection Direction { get; }

        /// <summary>
        /// If set to true, the packet is dropped and not forwarded.
        /// </summary>
        public bool Drop { get; set; }

        /// <summary>
        /// If set to true, the original raw bytes are forwarded without re-serialization.
        /// Use this to avoid re-encoding costs if no changes were made.
        /// </summary>
        public bool Bypass { get; set; }
        
        /// <summary>
        /// The original raw bytes of the packet. 
        /// Populated by the proxy engine.
        /// </summary>
        public ReadOnlyMemory<byte> Raw { get; }

        // TODO: Add Session References if needed (e.g. ISessionContext)
        // public ISessionContext Client { get; }
        // public ISessionContext Server { get; }

        public ProxyPacketContext(Packet packet, PacketDirection direction, ReadOnlyMemory<byte> raw)
        {
            Packet = packet;
            Direction = direction;
            Raw = raw;
        }
    }
}
