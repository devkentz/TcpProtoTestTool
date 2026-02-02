using System.Net.Sockets;

namespace ProtoTestTool.Network
{
    public class SimpleTcpClient : NetCoreServer.TcpClient
    {
        public event Action<byte[]>? DataReceived;
        public event Action? Connected;
        public event Action? Disconnected;
        public event Action<SocketError>? ErrorOccurred;

        private TaskCompletionSource<bool>? _connectTcs;
        private readonly ManualResetEventSlim _disconnectEvent = new(true);

        public SimpleTcpClient(string address, int port) : base(address, port)
        {
        }

        public Task<bool> ConnectWithResultAsync()
        {
            _connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disconnectEvent.Reset();
            ConnectAsync();
            return _connectTcs.Task;
        }

        public void DisconnectAndStop()
        {
            DisconnectAsync();
            _disconnectEvent.Wait(TimeSpan.FromSeconds(5));
        }

        protected override void OnConnected()
        {
            _connectTcs?.TrySetResult(true);
            Connected?.Invoke();
        }

        protected override void OnDisconnected()
        {
            _connectTcs?.TrySetResult(false);
            _disconnectEvent.Set();
            Disconnected?.Invoke();
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            var receivedData = new byte[size];
            Array.Copy(buffer, offset, receivedData, 0, size);

            DataReceived?.Invoke(receivedData);
        }

        protected override void OnError(SocketError error)
        {
            _connectTcs?.TrySetResult(false);
            ErrorOccurred?.Invoke(error);
        }
    }
}
