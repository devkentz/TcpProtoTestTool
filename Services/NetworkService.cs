using System;
using System.Threading.Tasks;
using ProtoTestTool.Network;

namespace ProtoTestTool.Services
{
    public class NetworkService : INetworkService
    {
        private SimpleTcpClient? _client;
        
        public event Action? Connected;
        public event Action? Disconnected;
        public event Action<string>? ErrorOccurred;
        public event Action<byte[]>? DataReceived;

        public bool IsConnected => _client?.IsConnected ?? false;

        public async Task ConnectAsync(string ip, int port)
        {
            Disconnect(); // Ensure previous connection is closed

            _client = new SimpleTcpClient(ip, port);
            
            // Forward events
            _client.Connected += () => Connected?.Invoke();
            _client.Disconnected += () => Disconnected?.Invoke();
            _client.ErrorOccurred += (err) => ErrorOccurred?.Invoke(err);
            _client.DataReceived += (bytes) => DataReceived?.Invoke(bytes);

            // SimpleTcpClient.ConnectAsync is theoretically async but current impl might be fire-and-forget or instant.
            // Adjust based on actual SimpleTcpClient implementation.
            _client.ConnectAsync();
            
            await Task.CompletedTask;
        }

        public void Disconnect()
        {
            if (_client != null)
            {
                _client.DisconnectAndStop();
                _client = null;
            }
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data)
        {
            if (_client == null || !_client.IsConnected) return; // Or throw

            // SimpleTcpClient.SendAsync takes Span, but we can't await void or non-Task.
            // Assuming SimpleTcpClient.SendAsync is void or fire-and-forget.
            _client.SendAsync(data.Span);
            
            await Task.CompletedTask;
        }
    }
}
