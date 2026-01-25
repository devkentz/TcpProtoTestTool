using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace ProtoTestTool.ScriptContract
{
    public class ScriptStateStore : IScriptStateStore
    {
        private readonly ConcurrentDictionary<string, object?> _memoryStore = new();
        private readonly string _connectionString;

        public ScriptStateStore(string dbPath = "client_cache.db")
        {
            _connectionString = $"Data Source={dbPath}";
            InitializeDatabase();
            LoadFromPersistent();
        }

        private void InitializeDatabase()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = 
                    @"CREATE TABLE IF NOT EXISTS KeyValueStore (
                        Key TEXT PRIMARY KEY, 
                        Value TEXT, 
                        Type TEXT,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    );";
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScriptStateStore] DB Init Failed: {ex.Message}");
            }
        }

        private void LoadFromPersistent()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Key, Value, Type FROM KeyValueStore";
                
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var json = reader.GetString(1);
                    var typeName = reader.GetString(2);

                    try 
                    {
                        var type = Type.GetType(typeName);
                        if (type != null)
                        {
                            var value = JsonConvert.DeserializeObject(json, type);
                            if (value != null)
                            {
                                _memoryStore[key] = value;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore de-serialization errors for cache
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScriptStateStore] Load Failed: {ex.Message}");
            }
        }

        public T Get<T>(string key)
        {
            if (_memoryStore.TryGetValue(key, out var value))
            {
                if (value is T typedValue)
                    return typedValue;
                
                // Try convert (e.g. Int64 to Int32 common in JSON)
                if (value != null)
                {
                    try { return (T)Convert.ChangeType(value, typeof(T)); }
                    catch { }
                }
            }
            throw new System.Collections.Generic.KeyNotFoundException($"Key '{key}' not found in StateStore.");
        }

        public bool TryGet<T>(string key, out T value)
        {
            if (_memoryStore.TryGetValue(key, out var obj))
            {
                if (obj is T typedValue)
                {
                    value = typedValue;
                    return true;
                }
                try 
                { 
                    if (obj != null)
                    {
                        value = (T)Convert.ChangeType(obj, typeof(T));
                        return true;
                    }
                }
                catch { }
            }
            value = default!;
            return false;
        }

        public void Set<T>(string key, T value)
        {
            _memoryStore[key] = value;
        }

        public void Clear()
        {
            _memoryStore.Clear();
        }

        public void FlushToPersistent()
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                using var transaction = connection.BeginTransaction();
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                
                command.CommandText = 
                    @"INSERT OR REPLACE INTO KeyValueStore (Key, Value, Type, UpdatedAt) 
                      VALUES ($key, $value, $type, CURRENT_TIMESTAMP)";
                
                var pKey = command.CreateParameter(); pKey.ParameterName = "$key"; command.Parameters.Add(pKey);
                var pValue = command.CreateParameter(); pValue.ParameterName = "$value"; command.Parameters.Add(pValue);
                var pType = command.CreateParameter(); pType.ParameterName = "$type"; command.Parameters.Add(pType);

                foreach (var kvp in _memoryStore)
                {
                    if (kvp.Value == null) continue;
                    pKey.Value = kvp.Key;
                    pValue.Value = JsonConvert.SerializeObject(kvp.Value);
                    pType.Value = kvp.Value.GetType().AssemblyQualifiedName; // Store full type info
                    
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScriptStateStore] Flush Failed: {ex.Message}");
                throw;
            }
        }
    }
}
