using System.Net;
using System.Net.Sockets;
using NetCoreServer;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool.Network
{
    public class ReverseProxyServer : TcpServer
    {
        private readonly string _upstreamIp;
        private readonly int _upstreamPort;
        private readonly ProxyInterceptorPipeline _pipeline;
        private readonly IPacketCodec _codec;

        public ReverseProxyServer(string address, int port, string upstreamIp, int upstreamPort, ProxyInterceptorPipeline pipeline, IPacketCodec codec) 
            : base(IPAddress.Parse(address), port)
        {
            _upstreamIp = upstreamIp;
            _upstreamPort = upstreamPort;
            _pipeline = pipeline;
            _codec = codec;
        }

        protected override TcpSession CreateSession()
        {
            return new ProxySession(this, _upstreamIp, _upstreamPort, _pipeline, _codec);
        }

        protected override void OnError(SocketError error)
        {
            Console.WriteLine($"[ProxyServer] Error: {error}");
        }
    }
}
