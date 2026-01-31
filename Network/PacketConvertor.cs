using Google.Protobuf;
using Google.Protobuf.Reflection;
using Newtonsoft.Json;


namespace ProtoTestTool.Network;

public class PacketConvertor
{
    public override string ToString() => Name;

    public required Type Type { get; set; }
    public required string Name { get; set; }
    public string? JsonText { get; set; }

    public string DefaultJsonString()
    {
        if (JsonText != null)
            return JsonText;

        var instance = Activator.CreateInstance(Type);
        if (instance == null)
            return "{}";

        ObjectInitializer.EnsureNonNullFields(instance, addDefaultElements: true);

        // Fallback for non-IMessage types
        JsonText = JsonConvert.SerializeObject(instance, Formatting.Indented);
        return JsonText;
    }

    public IMessage ToPacket(string jsonStr)
    {
        JsonText = jsonStr;
        return (IMessage)JsonConvert.DeserializeObject(jsonStr, Type)!;
    }
}