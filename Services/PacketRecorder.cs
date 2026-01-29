using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using ProtoTestTool.ScriptContract;
using System.Text.Encodings.Web;

namespace ProtoTestTool.Services
{
    public class RecordedPacket
    {
        public string Time { get; set; } = "";
        public string Direction { get; set; } = "";
        public string PacketName { get; set; } = "";
        public object? Header { get; set; } // Protocol Header object
        public object? Payload { get; set; } // Serialized JSON object
    }

    public class PacketRecorder
    {
        // Separate instances for Client and Proxy
        public static readonly PacketRecorder Client = new PacketRecorder("client");
        public static readonly PacketRecorder Proxy = new PacketRecorder("proxy");

        private readonly string _prefix;

        public PacketRecorder(string prefix)
        {
            _prefix = prefix;
        }

        private readonly ConcurrentQueue<RecordedPacket> _buffer = new();
        private bool _isRecording;
        private string _currentFilePath = "";

        public bool IsRecording => _isRecording;

        public void Start(string workspacePath)
        {
            if (_isRecording) Stop();

            var recordingsDir = Path.Combine(workspacePath, "Recordings");
            if (!Directory.Exists(recordingsDir))
                Directory.CreateDirectory(recordingsDir);

            var fileName = $"{_prefix}_recording_{DateTime.Now:yyyyMMdd_HHmmss}.jsonp";
            _currentFilePath = Path.Combine(recordingsDir, fileName);
            
            _buffer.Clear();
            _isRecording = true;
        }

        public void Stop()
        {
            if (!_isRecording) return;
            _isRecording = false;

            FlushToFile();
        }

        private void FlushToFile()
        {
            try
            {
                var list = new List<RecordedPacket>(_buffer);
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                var json = JsonSerializer.Serialize(list, options);

                // Writing as pure JSON for now. If callback wrapper is needed, can be added here.
                File.WriteAllText(_currentFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save recording: {ex.Message}");
            }
        }

        public void Record(PacketDirection direction, Packet packet)
        {
            if (!_isRecording) return;

            try
            {
                // Format Payload as JSON
                var formatter = new JsonFormatter(JsonFormatter.Settings.Default);
                var jsonString = formatter.Format(packet.Message);
                
                var payloadObj = JsonSerializer.Deserialize<object>(jsonString);

                var record = new RecordedPacket
                {
                    Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                    Direction = direction.ToString(),
                    PacketName = packet.Message.Descriptor.FullName, // Changed to FullName for type resolution
                    Header = packet.Header,
                    Payload = payloadObj
                };

                _buffer.Enqueue(record);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error recording packet: {ex.Message}");
            }
        }
    }
}
