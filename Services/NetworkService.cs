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
            Disconnect();

            _client = new SimpleTcpClient(ip, port);

            _client.Connected += () => Connected?.Invoke();
            _client.Disconnected += () => Disconnected?.Invoke();
            _client.ErrorOccurred += (err) => ErrorOccurred?.Invoke(err.ToString());
            _client.DataReceived += (bytes) => DataReceived?.Invoke(bytes);

            var connected = await _client.ConnectWithResultAsync();
            if (!connected)
                throw new InvalidOperationException($"Failed to connect to {ip}:{port}");
        }

        public void Disconnect()
        {
            if (_client != null)
            {
                _client.DisconnectAndStop();
                _client = null;
            }
        }

        public Task SendAsync(ReadOnlyMemory<byte> data)
        {
            if (_client == null || !_client.IsConnected)
                throw new InvalidOperationException("Not connected");

            _client.SendAsync(data.Span);
            return Task.CompletedTask;
        }
    }
}
