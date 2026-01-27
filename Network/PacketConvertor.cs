using Google.Protobuf;
using Newtonsoft.Json;


namespace ProtoTestTool.Network;

public class PacketConvertor
{
	public override string ToString() => Name;

	public required Type Type { get; set; }
	public required string Name { get; set; }
	public string? JsonText { get; set; }

	public (string name, string json) DefaultJsonString()
	{
		if (JsonText != null)
		{
			return (Name, JsonText);  // 생성된 객체를 JSON 문자열로 변환합니다.
		}

		var instance = Activator.CreateInstance(Type);  // Type에서 객체 인스턴스를 생성합니다.
		if(instance == null)
			return default;
		
		ObjectInitializer.EnsureNonNullFields(instance);

		JsonText = JsonConvert.SerializeObject(instance, Formatting.Indented);
		return (Name, JsonText);  // 생성된 객체를 JSON 문자열로 변환합니다.
	}

	public IMessage ToPacket(string jsonStr)
	{
		JsonText = jsonStr;
		return (IMessage)JsonConvert.DeserializeObject(jsonStr, Type)!;
	}
}