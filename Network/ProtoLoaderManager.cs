using System.Collections.Frozen;

namespace ProtoTestTool.Network
{
    public class ProtoLoaderManager
    {
        public FrozenDictionary<string, PacketConvertor> PacketsByName { get; private set; } = FrozenDictionary<string, PacketConvertor>.Empty; 

        // Request -> Response 매핑
        public FrozenDictionary<string, string> RequestToResponse { get; private set; } = FrozenDictionary<string, string>.Empty;

        private static readonly Lazy<ProtoLoaderManager> SInstance = new Lazy<ProtoLoaderManager>(() => new ProtoLoaderManager());
        public static ProtoLoaderManager Instance => SInstance.Value;

        public IReadOnlyList<PacketConvertor> GetIMessages() => PacketsByName.Values;

        // Runtime Registration
 
        public void InitPacket(IReadOnlyList<Type> types)
        {
            var newPackets = new Dictionary<string, PacketConvertor>();

            foreach (var type in types)
                newPackets[type.Name] = new PacketConvertor {Name = type.Name, Type = type};

            PacketsByName = newPackets.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        public void Clear()
        {
            PacketsByName = FrozenDictionary<string, PacketConvertor>.Empty;
            RequestToResponse = FrozenDictionary<string, string>.Empty;
        }
    }
}