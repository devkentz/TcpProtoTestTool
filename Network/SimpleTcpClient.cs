using System.Net.Sockets;

namespace ProtoTestTool.Network
{
    public class SimpleTcpClient : NetCoreServer.TcpClient
    {
        public event Action<byte[]>? DataReceived;
        public event Action? Connected;
        public event Action? Disconnected;
        public event Action<SocketError>? ErrorOccurred;

        public SimpleTcpClient(string address, int port) : base(address, port)
        {
        }

        public void DisconnectAndStop()
        {
            DisconnectAsync();
            
            while (IsConnected)
                Thread.Yield();
        }

        protected override void OnConnected()
        {
            Connected?.Invoke();
        }

        protected override void OnDisconnected()
        {
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
            ErrorOccurred?.Invoke(error);
        }
    }
}