using System;
using System.Collections.Generic;
using Google.Protobuf;


namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Interface responsible for providing information about available packet types.
    /// </summary>
    public interface IPacketRegistry
    {
        /// <summary>
        /// Returns all available message types defined in the loaded protocol.
        /// </summary>
        IEnumerable<Type> GetMessageTypes();

        /// <summary>
        /// Gets the message type for a specific ID.
        /// </summary>
        Type? GetMessageType(int msgId);

        /// <summary>
        /// Gets the ID for a specific message type.
        /// </summary>
        int GetMsgId(Type type);

        /// <summary>
        /// Registers a packet type with an ID.
        /// </summary>
        /// <param name="msgId">The unique message ID.</param>
        /// <param name="type">The C# type of the message.</param>
        /// <param name="msgName">Optional. The message name (defaults to Type.Name).</param>
        /// <param name="isRequest">Optional. If true, registers as a Send Packet. If false, Receive Packet. If null, just Generic.</param>
        void Register(int msgId, Type type, string? msgName = null, bool? isRequest = null);
        
        MessageParser GetParserById(int msgId); 
    }
}