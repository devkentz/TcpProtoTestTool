using System.IO;
using System.Text.Json;
using System.Windows.Media; // For Brush
using Google.Protobuf;
using Google.Protobuf.Reflection;
using ProtoTestTool.Network;

namespace ProtoTestTool.Services
{
    public class ReplayService : IReplayService
    {
        public async Task<List<RecordedPacket>> LoadRecordingAsync(string filePath)
        {
            if (!File.Exists(filePath)) 
                throw new FileNotFoundException("Recording file not found", filePath);

            var json = await File.ReadAllTextAsync(filePath);
            var packets = JsonSerializer.Deserialize<List<RecordedPacket>>(json, new JsonSerializerOptions 
            { 
                 PropertyNameCaseInsensitive = true 
            });

            return packets ?? new List<RecordedPacket>();
        }

        public async Task ReplayAllAsync(List<RecordedPacket> packets, Func<IMessage, object?, Task> sendCallback, Action<string, Brush> logger)
        {
            if (packets.Count == 0) 
                return;

            foreach (var record in packets)
            {
                // Only replay Outbound
                if (record.Direction != "Outbound") continue;

                // Resolve Type
                var packetConvertor = ProtoLoaderManager.Instance.PacketsByName.Values
                    .FirstOrDefault(p => 
                    {
                        var desc = p.Type.GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as MessageDescriptor;
                        return desc != null && desc.FullName == record.PacketName;
                    });

                if (packetConvertor?.Type == null)
                {
                    logger?.Invoke($"[Replay Skip] Unknown Type: {record.PacketName}", Brushes.Yellow);
                    continue;
                }
                
                var msgDescriptor = packetConvertor.Type.GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as MessageDescriptor;
                if (msgDescriptor == null) continue;

                // Deserialization
                IMessage? protoMsg = null;
                try
                {
                    if (record.Payload is JsonElement je)
                    {
                        var jsonPayload = je.GetRawText();
                        var parser = new JsonParser(JsonParser.Settings.Default);
                        protoMsg = parser.Parse(jsonPayload, msgDescriptor);
                    }
                    else if (record.Payload != null)
                    {
                         // Fallback objects
                         var jsonPayload = JsonSerializer.Serialize(record.Payload);
                         var parser = new JsonParser(JsonParser.Settings.Default);
                         protoMsg = parser.Parse(jsonPayload, msgDescriptor);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Replay Error] Deserialization failed for {record.PacketName}: {ex.Message}", Brushes.Red);
                    continue;
                }

                if (protoMsg != null)
                {
                    await sendCallback(protoMsg, record.Header);
                    await Task.Delay(10); // Small throttle
                }
            }
        }
    }
}
