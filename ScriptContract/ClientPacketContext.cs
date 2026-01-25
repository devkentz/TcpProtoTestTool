
using Google.Protobuf;

namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Context for Test Client Mode interceptors.
    /// Allows modification of the message BEFORE serialization.
    /// </summary>
    public sealed class ClientPacketContext
    {
        /// <summary>
        /// The message object to be sent.
        /// You can modify properties of this object.
        /// </summary>
        public IMessage Message { get; set; }

        /// <summary>
        /// Key-Value metadata/headers from UI.
        /// </summary>
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();

        public ClientPacketContext(IMessage message)
        {
            Message = message;
        }
    }
}
