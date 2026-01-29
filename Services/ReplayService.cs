using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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

        public async Task ReplayAllAsync(List<RecordedPacket> packets, Func<IMessage, object, Task> sendCallback, Action<string, Brush> logger)
        {
            if (packets == null || packets.Count == 0) return;

            foreach (var record in packets)
            {
                // Only replay Outbound
                if (record.Direction != "Outbound") continue;

                // Resolve Type
                var packetConvertor = ProtoLoaderManager.Instance.PacketsByMsgId.Values
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

                // Deserialize Payload
                IMessage protoMsg = null;
                try
                {
                    if (record.Payload is JsonElement je)
                    {
                        var jsonPayload = je.GetRawText();
                        var parser = new JsonParser(JsonParser.Settings.Default);
                        var descriptor = packetConvertor.Type.GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as MessageDescriptor;
                         protoMsg = parser.Parse(jsonPayload, descriptor);
                    }
                    else if (record.Payload != null)
                    {
                         // Fallback objects
                         var jsonPayload = JsonSerializer.Serialize(record.Payload);
                         var parser = new JsonParser(JsonParser.Settings.Default);
                         var descriptor = packetConvertor.Type.GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as MessageDescriptor;
                         protoMsg = parser.Parse(jsonPayload, descriptor);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Invoke($"[Replay Error] Deserialization failed for {record.PacketName}: {ex.Message}", Brushes.Red);
                    continue;
                }

                if (protoMsg != null)
                {
                    // Deserialize Header (Generic object -> Concrete Header is handled by Pipeline usually)
                    // But here we pass 'object' header from record
                    // Ideally we should convert it to IHeader?
                    // MainWindow logic casts `packet.Header as IHeader` but record.Header is `object` (JsonElement).
                    
                    // For now, pass the record.Header (JsonElement) and let the Callback/Pipeline handle it? 
                    // Pipeline expects `IHeader`.
                    // We need to deserialize Header here if we want to be clean.
                    // But Header type depends on Protocol?
                    
                    // MainWindow uses `JsonConvert.DeserializeObject<BaseHeader>(json)` usually.
                    // Let's rely on the callback to handle the Header mapping if possible, 
                    // or we deserialize to dynamic/BaseHeader here.
                    
                    // Simple approach: Pass the header object, let MainWindow logic (which knows Header type) handle it?
                    // But sendCallback signature is Func<IMessage, object, Task>.
                    
                    await sendCallback(protoMsg, record.Header);
                    await Task.Delay(10); // Small throttle
                }
            }
        }

        // Interface implementation match
        public Task ReplayAllAsync(List<RecordedPacket> packets, SimpleTcpClient client, Action<string, Brush> logger)
        {
             // Overload for direct usage if needed, but we prefer the Callback approach for decoupling pipeline.
             // We can throw NotSupported or implement a basic send.
             throw new NotImplementedException("Use the callback overload for pipeline integration.");
        }
    }
}
