using System.Net;
using System.Net.Sockets;
using NetCoreServer;
using ProtoTestTool.ScriptContract;


namespace ProtoTestTool.Network
{
    public class ProxyServer : TcpServer
    {
        private readonly string _upstreamIp;
        private readonly int _upstreamPort;
        private readonly ProxyInterceptorPipeline _pipeline;

        public ProxyServer(string address, int port, string upstreamIp, int upstreamPort, ProxyInterceptorPipeline pipeline) 
            : base(IPAddress.Parse(address), port)
        {
            _upstreamIp = upstreamIp;
            _upstreamPort = upstreamPort;
            _pipeline = pipeline;
        }

        protected override TcpSession CreateSession()
        {
            return new ProxySession(this, _upstreamIp, _upstreamPort, _pipeline);
        }

        protected override void OnError(SocketError error)
        {
            System.Diagnostics.Debug.WriteLine($"[ProxyServer] Error: {error}");
        }
    }
}
