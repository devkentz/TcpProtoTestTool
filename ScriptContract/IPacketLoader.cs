using System;

namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Interface for scriptable packet registration.
    /// Scripts should implement this interface to perform custom packet registration logic.
    /// </summary>
    public interface IPacketLoader
    {
        /// <summary>
        /// Loads and registers packets into the registry.
        /// </summary>
        /// <param name="registry">The packet registry to register packets into.</param>
        void Load(IPacketRegistry registry);
    }
}
