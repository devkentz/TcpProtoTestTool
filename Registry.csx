using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Google.Protobuf;
using ProtoTestTool.ScriptContract;

public class PacketRegistry : IPacketRegistry
{
    private Dictionary<int, Type> _idToType = new Dictionary<int, Type>();
    private Dictionary<Type, int> _typeToId = new Dictionary<Type, int>();
    private readonly Action<string> _logger;

    public PacketRegistry(Action<string> logger)
    {
        _logger = logger ?? Console.WriteLine;
    }

    public void Register(int id, Type type)
    {
        if (_idToType.ContainsKey(id))
        {
            _logger($"[Registry] Warning: Duplicate ID {id} for {type.Name}");
        }
        _idToType[id] = type;
        _typeToId[type] = id;
    }

    public IEnumerable<Type> GetMessageTypes() => _idToType.Values;

    public Type? GetMessageType(int id) => _idToType.TryGetValue(id, out var type) ? type : null;

    public int GetMessageId(Type type) => _typeToId.TryGetValue(type, out var id) ? id : 0;
}