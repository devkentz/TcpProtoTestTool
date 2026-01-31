using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using Google.Protobuf;

using Google.Protobuf.Reflection;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool.Network
{
    public class ProtoLoaderManager : IPacketRegistry
    {
        public FrozenDictionary<string, PacketConvertor> PacketsByName { get; private set; } = FrozenDictionary<string, PacketConvertor>.Empty;
        public FrozenDictionary<string, PacketConvertor> SendPackets { get; private set; } = FrozenDictionary<string, PacketConvertor>.Empty;
        public FrozenDictionary<string, PacketConvertor> ReceivePackets { get; private set; } = FrozenDictionary<string, PacketConvertor>.Empty;
        // Request -> Response 매핑
        public FrozenDictionary<string, string> RequestToResponse { get; private set; } = FrozenDictionary<string, string>.Empty;
        
        private static readonly Lazy<ProtoLoaderManager> SInstance = new Lazy<ProtoLoaderManager>(() => new ProtoLoaderManager());
        public static ProtoLoaderManager Instance => SInstance.Value;

        public void LoadAllProtos(string protoDirectory = "")
        {
            var assembliesByName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

            // 1. Dynamic Compilation
            if (!string.IsNullOrEmpty(protoDirectory) && Directory.Exists(protoDirectory))
            {
                try
                {
                    var asm = Services.ProtoCompiler.Compile(protoDirectory);
                    if (asm != null)
                    {
                        assembliesByName[asm.GetName().Name ?? asm.FullName ?? "Unknown"] = asm;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Proto compilation failed: {ex.Message}");
                    throw;
                }
            }

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            // Only load the specific Protos.dll
            var dllFiles = Directory.GetFiles(baseDirectory, "Protos.dll", SearchOption.TopDirectoryOnly).ToList();
            
            var protoGenDir = Path.Combine(baseDirectory, "ProtoGen");
            if (Directory.Exists(protoGenDir))
            {
                dllFiles.AddRange(Directory.GetFiles(protoGenDir, "Protos.dll", SearchOption.TopDirectoryOnly));
            }

            // 이미 로드된 어셈블리 먼저 추가
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            {
                assembliesByName[loaded.FullName ?? loaded.GetName().Name!] = loaded;
            }

            // DLL 파일 로드
            foreach (var dllPath in dllFiles)
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(dllPath);
                    var fullName = assemblyName.FullName!;

                    if (!assembliesByName.ContainsKey(fullName))
                    {
                        var assembly = Assembly.LoadFrom(dllPath);
                        assembliesByName[fullName] = assembly;
                        Console.WriteLine($"Loaded: {assembly.GetName().Name}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Skip: {Path.GetFileName(dllPath)} - {ex.Message}");
                }
            }

            Console.WriteLine($"\nTotal assemblies: {assembliesByName.Count}");

            // 2. IMessage 타입 수집 (한 번의 순회로)
            var allMessageTypes = assembliesByName.Values
                .AsParallel() // 병렬 처리로 성능 향상
                .SelectMany(assembly => GetMessageTypes(assembly))
                .ToList();

            Console.WriteLine($"Found {allMessageTypes.Count} proto messages\n");

            // 3. 패킷 분류 및 Dictionary 생성
            var sendPacketsDict = new Dictionary<string, PacketConvertor>(StringComparer.OrdinalIgnoreCase);
            var receivePacketsDict = new Dictionary<string, PacketConvertor>(StringComparer.OrdinalIgnoreCase);
            var allPacketsDict = new Dictionary<string, PacketConvertor>(StringComparer.OrdinalIgnoreCase);
            var reqToResMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in allMessageTypes)
            {
                var name = type.Name;
                var convertor = new PacketConvertor {Name = name, Type = type};
                
                allPacketsDict[name] = convertor;

                if (name.EndsWith("Req"))
                {
                    sendPacketsDict[name] = convertor;

                    // Request -> Response 매핑 생성
                    // LoginReq -> LoginRes
                    var baseName = name[..^3]; // "Req" 제거
                    var responseName = baseName + "Res";
                    reqToResMapping[name] = responseName;
                }
                else if (name.EndsWith("Res") || name.EndsWith("Notify") || name.EndsWith("NotifyMsg"))
                {
                    receivePacketsDict[name] = convertor;
                }
            }

            // 4. FrozenDictionary로 변환 (읽기 전용 최적화)
            PacketsByName = allPacketsDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            SendPackets = sendPacketsDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            ReceivePackets = receivePacketsDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            RequestToResponse = reqToResMapping.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            Console.WriteLine($"Send packets: {SendPackets.Count}");
            Console.WriteLine($"Receive packets: {ReceivePackets.Count}");
            Console.WriteLine($"Request-Response pairs: {RequestToResponse.Count}");
        }

        private static IEnumerable<Type> GetMessageTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes()
                    .Where(type =>
                        typeof(IMessage).IsAssignableFrom(type) &&
                        type is {IsAbstract: false, IsInterface: false, IsGenericType: false});
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 로드 가능한 타입만 반환
                return ex.Types.Where(t =>
                    t != null &&
                    typeof(IMessage).IsAssignableFrom(t) &&
                    t is {IsAbstract: false, IsInterface: false, IsGenericType: false})!;
            }
            catch
            {
                return Enumerable.Empty<Type>();
            }
        }

        // Request에 대응하는 Response 찾기
        public PacketConvertor? GetResponseFor(string requestName)
        {
            if (RequestToResponse.TryGetValue(requestName, out var responseName))
            {
                ReceivePackets.TryGetValue(responseName, out var response);
                return response;
            }

            return null;
        }

        // Response에 대응하는 Request 찾기
        public PacketConvertor? GetRequestFor(string responseName)
        {
            var requestName = responseName.EndsWith("Res")
                ? responseName[..^3] + "Req"
                : null;

            if (requestName != null && SendPackets.TryGetValue(requestName, out var request))
            {
                return request;
            }

            return null;
        }

        public PacketConvertor? Find(string name) => PacketsByName.GetValueOrDefault(name);

        public IReadOnlyList<PacketConvertor> GetSendPackets() 
        {
            // If user has defined specific Send packets, use them.
            // Otherwise, fallback to showing all registered packets (Default behavior).
            if (SendPackets != null && SendPackets.Count > 0)
                return SendPackets.Values.ToList();
                
            return PacketsByName.Values.ToList();
        }
        
        // Runtime Registration
        public void RegisterPacket(Type type)
        {
            var name = type.Name;
            var convertor = new PacketConvertor { Name = name, Type = type };
            
            // Default only adds to generic lookup
            var newPackets = new Dictionary<string, PacketConvertor>(PacketsByName ?? FrozenDictionary<string, PacketConvertor>.Empty);
            newPackets[name] = convertor;
            PacketsByName = newPackets.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        public void Clear()
        {
            PacketsByName = FrozenDictionary<string, PacketConvertor>.Empty;
            SendPackets = FrozenDictionary<string, PacketConvertor>.Empty;
            ReceivePackets = FrozenDictionary<string, PacketConvertor>.Empty;
            RequestToResponse = FrozenDictionary<string, string>.Empty;
            _idToType.Clear();
            _typeToId.Clear();
        }

        // IPacketRegistry Implementation
        private readonly Dictionary<int, Type> _idToType = new();
        private readonly Dictionary<Type, int> _typeToId = new();

        public IEnumerable<Type> GetMessageTypes() => PacketsByName.Values.Select(p => p.Type).Distinct();

        public Type? GetMessageType(int msgId) => _idToType.GetValueOrDefault(msgId);

        public int GetMsgId(Type type) => _typeToId.GetValueOrDefault(type, 0);

        public void Register(int msgId, Type type, string? msgName = null, bool? isRequest = null)
        {
            _idToType[msgId] = type;
            _typeToId[type] = msgId;

            var name = msgName ?? type.Name;
            
            // 1. Generic Registration (Lookup by Name)
            var convertor = new PacketConvertor { Name = name, Type = type };
            var newPackets = new Dictionary<string, PacketConvertor>(PacketsByName ?? FrozenDictionary<string, PacketConvertor>.Empty);
            newPackets[name] = convertor;
            PacketsByName = newPackets.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            // 2. Optional: Register as Send (Request) or Receive
            if (isRequest == true)
            {
                var newSend = new Dictionary<string, PacketConvertor>(SendPackets ?? FrozenDictionary<string, PacketConvertor>.Empty);
                newSend[name] = convertor;
                SendPackets = newSend.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            }
            else if (isRequest == false) 
            {
                var newRecv = new Dictionary<string, PacketConvertor>(ReceivePackets ?? FrozenDictionary<string, PacketConvertor>.Empty);
                newRecv[name] = convertor;
                ReceivePackets = newRecv.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            }
        }

        public MessageParser GetParserById(int msgId)
        {
            if (_idToType.TryGetValue(msgId, out var type))
            {
                var prop = type.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static);
                if (prop != null) return (MessageParser)prop.GetValue(null)!;
            }
            throw new ArgumentException($"Parser not found for ID {msgId}");
        }
    }
}