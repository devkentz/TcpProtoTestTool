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

        MessageParser GetParserById(int msgId); 
    }
}