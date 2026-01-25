using System;
using System.Collections.Generic;
using System.Reflection;
using ProtoTestTool.ScriptContract;

// ************************************************************
// * Packet Registry Script
// * 
// * Use this script to map Message IDs (int) to C# Types.
// * This is essential for the Serializer to know what type
// * to deserialize the payload into.
// ************************************************************

public class MyPacketRegistry : IPacketRegistry
{
    private Dictionary<int, Type> _idToType = new Dictionary<int, Type>();
    private Dictionary<Type, int> _typeToId = new Dictionary<Type, int>();

    public MyPacketRegistry()
    {
        // Example: Registering packets manually or via reflection
        // Register(1001, typeof(MyGame.LoginRequest));
        // Register(1002, typeof(MyGame.LoginResponse));
    }

    public void Register(int id, Type type)
    {
        _idToType[id] = type;
        _typeToId[type] = id;
    }

    public IEnumerable<Type> GetMessageTypes() => _idToType.Values;

    public Type? GetMessageType(int id) 
    {
        return _idToType.TryGetValue(id, out var type) ? type : null;
    }

    public int GetMessageId(Type type) 
    {
        return _typeToId.TryGetValue(type, out var id) ? id : 0;
    }
}