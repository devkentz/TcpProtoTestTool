using System.Reflection;

namespace ProtoTestTool.Services;

public class ProtobufHelper
{
    public static List<Type> GetIMessageTypes(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => typeof(Google.Protobuf.IMessage).IsAssignableFrom(t) && t is {IsInterface: false, IsAbstract: false})
            .ToList();
    }
}