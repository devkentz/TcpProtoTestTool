namespace ProtoTestTool.ScriptContract
{
    /// <summary>
    /// Provides access to state shared between scripts and the host application.
    /// Supports In-Memory (fast) and Persistent (SQLite) storage.
    /// </summary>
    public interface IScriptStateStore
    {
        /// <summary>
        /// Gets a value from the in-memory store. Throws if missing.
        /// </summary>
        T Get<T>(string key);

        /// <summary>
        /// Sets a value in the in-memory store.
        /// </summary>
        void Set<T>(string key, T value);

        /// <summary>
        /// Tries to get a value from the in-memory store.
        /// </summary>
        bool TryGet<T>(string key, out T value);

        /// <summary>
        /// Persists the current in-memory state to the local SQLite database.
        /// Call this only when necessary (e.g. end of test case).
        /// </summary>
        void FlushToPersistent();
    }
}
