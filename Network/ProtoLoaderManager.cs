using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using Google.Protobuf;

namespace ProtoTestTool.Network
{
    public class ProtoLoaderManager
    {
        public FrozenDictionary<string, PacketConvertor> PacketsByMsgId { get; private set; } = null!;
        public FrozenDictionary<string, PacketConvertor> SendPackets { get; private set; } = null!;
        public FrozenDictionary<string, PacketConvertor> ReceivePackets { get; private set; } = null!;
        // Request -> Response 매핑
        public FrozenDictionary<string, string> RequestToResponse { get; private set; } = null!;
        
        private static readonly Lazy<ProtoLoaderManager> SInstance = new Lazy<ProtoLoaderManager>(() => new ProtoLoaderManager());
        public static ProtoLoaderManager Instance => SInstance.Value;

        public void LoadAllProtos()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            // Only load the specific Protos.dll
            var dllFiles = Directory.GetFiles(baseDirectory, "Protos.dll", SearchOption.TopDirectoryOnly).ToList();
            
            var protoGenDir = Path.Combine(baseDirectory, "ProtoGen");
            if (Directory.Exists(protoGenDir))
            {
                dllFiles.AddRange(Directory.GetFiles(protoGenDir, "Protos.dll", SearchOption.TopDirectoryOnly));
            }

            // 1. 어셈블리 로드 (중복 제거를 위해 Dictionary 사용)
            var assembliesByName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

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
            PacketsByMsgId = allPacketsDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
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

        public PacketConvertor? Find(string name) => PacketsByMsgId.GetValueOrDefault(name);

        public IReadOnlyList<PacketConvertor> GetSendPackets() => SendPackets.Values;
        
        // Runtime Registration
        public void RegisterPacket(Type type)
        {
            var name = type.Name;
            var convertor = new PacketConvertor { Name = name, Type = type };
            
            var newPackets = new Dictionary<string, PacketConvertor>(PacketsByMsgId ?? FrozenDictionary<string, PacketConvertor>.Empty);
            newPackets[name] = convertor;
            PacketsByMsgId = newPackets.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            if (name.EndsWith("Req"))
            {
                var newSend = new Dictionary<string, PacketConvertor>(SendPackets ?? FrozenDictionary<string, PacketConvertor>.Empty);
                newSend[name] = convertor;
                SendPackets = newSend.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                
                // Update Mapping
                 var baseName = name[..^3];
                 var responseName = baseName + "Res";
                 var newMap = new Dictionary<string, string>(RequestToResponse ?? FrozenDictionary<string, string>.Empty);
                 newMap[name] = responseName;
                 RequestToResponse = newMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            }
            else if (name.EndsWith("Res") || name.EndsWith("Notify") || name.EndsWith("NotifyMsg"))
            {
                var newRecv = new Dictionary<string, PacketConvertor>(ReceivePackets ?? FrozenDictionary<string, PacketConvertor>.Empty);
                newRecv[name] = convertor;
                ReceivePackets = newRecv.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Clear()
        {
            PacketsByMsgId = FrozenDictionary<string, PacketConvertor>.Empty;
            SendPackets = FrozenDictionary<string, PacketConvertor>.Empty;
            ReceivePackets = FrozenDictionary<string, PacketConvertor>.Empty;
            RequestToResponse = FrozenDictionary<string, string>.Empty;
        }
    }
}