using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using ProtoTestTool.Network;
using ProtoTestTool.ScriptContract;

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

            return packets ?? [];
        }

        public async Task ReplayAllAsync(List<RecordedPacket> packets, Func<IMessage, object?, Task> sendCallback, Action<string, Brush> logger)
        {
            if (packets.Count == 0)
                return;

            // Build FullName → (PacketConvertor, MessageDescriptor) lookup once
            var lookup = new Dictionary<string, (PacketConvertor Convertor, MessageDescriptor Descriptor)>();

            foreach (var type in ScriptGlobals.Registry.GetMessageTypes())
            {
                if (type.GetProperty("Descriptor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        ?.GetValue(null) is MessageDescriptor desc)

                    lookup[desc.FullName] = (new PacketConvertor {Name = type.Name, Type = type}, desc);
            }

            foreach (var record in packets)
            {
                if (record.Direction != "Outbound") continue;

                if (!lookup.TryGetValue(record.PacketName, out var entry))
                {
                    logger?.Invoke($"[Replay Skip] Unknown Type: {record.PacketName}", Brushes.Yellow);
                    continue;
                }

                IMessage? protoMsg = null;
                try
                {
                    var parser = new JsonParser(JsonParser.Settings.Default);

                    if (record.Payload is JsonElement je)
                    {
                        protoMsg = parser.Parse(je.GetRawText(), entry.Descriptor);
                    }
                    else if (record.Payload != null)
                    {
                        var jsonPayload = JsonSerializer.Serialize(record.Payload);
                        protoMsg = parser.Parse(jsonPayload, entry.Descriptor);
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
                    await Task.Delay(10);
                }
            }
        }
    }
}