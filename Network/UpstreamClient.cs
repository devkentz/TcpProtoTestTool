using System;
using System.Net.Sockets;
using System.Text;
using NetCoreServer;

namespace ProtoTestTool.Network
{
    public class UpstreamClient : NetCoreServer.TcpClient
    {
        private readonly ProxySession _proxySession;

        public UpstreamClient(string address, int port, ProxySession proxySession) 
            : base(address, port)
        {
            _proxySession = proxySession;
        }

        protected override void OnConnected() { }

        protected override void OnDisconnected()
        {
            _proxySession.Disconnect();
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            // Forward data to ProxySession for Outbound processing
            _proxySession.OnUpstreamReceived(buffer, offset, size);
        }

        protected override void OnError(SocketError error) { }
    }
}
