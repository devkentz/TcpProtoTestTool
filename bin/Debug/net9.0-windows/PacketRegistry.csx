using System;
using System.Collections.Generic;
using ProtoTestTool.ScriptContract;

public class PacketRegistry : IPacketRegistry
{
    private readonly Dictionary<int, Type> _idToType = new Dictionary<int, Type>();
    private readonly Dictionary<Type, int> _typeToId = new Dictionary<Type, int>();

    public void Register(int id, Type type)
    {
        _idToType[id] = type;
        _typeToId[type] = id;
    }

    public IEnumerable<Type> GetMessageTypes() => _idToType.Values;

    public Type? GetMessageType(int id) => _idToType.TryGetValue(id, out var type) ? type : null;

    public int GetMessageId(Type type) => _typeToId.TryGetValue(type, out var id) ? id : 0;
}