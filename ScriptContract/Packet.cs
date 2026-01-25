using Google.Protobuf;

namespace ProtoTestTool.ScriptContract;

public class Packet
{
    public Packet(IHeader header, IMessage message)
    {
        Message = message;
        Header = header;
    }

    public IMessage Message { get; }
    public IHeader  Header { get; }
}