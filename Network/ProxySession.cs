using System.Buffers;
using NetCoreServer;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool.Network
{
    public class ProxySession : TcpSession
    {
        private readonly UpstreamClient _upstream;
        private readonly ProxyInterceptorPipeline _pipeline;
        private readonly IPacketCodec _codec;

        public ProxySession(TcpServer server, string upstreamIp, int upstreamPort, ProxyInterceptorPipeline pipeline, IPacketCodec codec) 
            : base(server)
        {
            _pipeline = pipeline;
            _codec = codec;
            _upstream = new UpstreamClient(upstreamIp, upstreamPort, this);
        }

        protected override void OnConnected()
        {
            // Connect to upstream server when client connects
            _upstream.ConnectAsync();
        }

        protected override void OnDisconnected()
        {
            _upstream.DisconnectAsync();
        }

        // Buffer for incoming data
        private readonly List<byte> _clientBuffer = new();
        private readonly List<byte> _serverBuffer = new();

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            // Client -> Proxy -> Server (Inbound)
            // Add to buffer
            lock (_clientBuffer)
            {
                for (long i = 0; i < size; i++)
                    _clientBuffer.Add(buffer[offset + i]);
            }
            _ = ProcessTrafficAsync(_clientBuffer, PacketDirection.Inbound);
        }

        // Called by UpstreamClient
        public void OnUpstreamReceived(byte[] buffer, long offset, long size)
        {
            // Server -> Proxy -> Client (Outbound)
            lock (_serverBuffer)
            {
                for (long i = 0; i < size; i++)
                    _serverBuffer.Add(buffer[offset + i]);
            }
            _ = ProcessTrafficAsync(_serverBuffer, PacketDirection.Outbound);
        }

        private async Task ProcessTrafficAsync(List<byte> bufferList, PacketDirection direction)
        {
            try
            {
                // Loop to consume all complete packets
                while (true)
                {
                    // Create ReadOnlySequence from current buffer window
                    // Note: This is inefficient (List -> Array -> ROS) but functional for prototype.
                    // Optimization: Use a circular buffer or Memory<byte> manager.
                    byte[] currentBytes;
                    lock (bufferList)
                    {
                        if (bufferList.Count == 0) return;
                        currentBytes = bufferList.ToArray();
                    }

                    ReadOnlySpan<byte> inputSpan = currentBytes.AsSpan();
                    
                    // Decode attempt
                    // We must pass a copy of logic reference to track consumption
                    var currentSeq = inputSpan;

                    var readSize = _codec.TryDecode(ref currentSeq, out var message);
                    if(readSize > 0)
                    {
                        // Calculate consumed amount
                        var consumed = inputSpan.Length - readSize;
                        
                        // Extract RAW bytes for the packet (from the original array)
                        var rawMemory = new ReadOnlyMemory<byte>(currentBytes, 0, (int)consumed);

                        // Remove consumed from List
                        lock (bufferList)
                        {
                            bufferList.RemoveRange(0, (int)consumed);
                        }
                        
                        // Process the packet
                        var context = new ProxyPacketContext(message!, direction, rawMemory);
                        
                        if (direction == PacketDirection.Inbound)
                            await _pipeline.RunInboundAsync(context);
                        else
                            await _pipeline.RunOutboundAsync(context);

                        if (context.Drop) continue;

                        // Forward
                        byte[] dataToSend;
                        if (context.Bypass && context.Raw.Length > 0)
                        {
                            dataToSend = context.Raw.ToArray();
                        }
                        else
                        {
                            var mem = _codec.Encode(context.Packet);
                            dataToSend = mem.ToArray();
                        }

                        if (direction == PacketDirection.Inbound)
                            _upstream.SendAsync(dataToSend);
                        else
                            SendAsync(dataToSend);
                    }
                    else
                    {
                        // Incomplete packet, wait for more data
                        break; 
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProxySession] Error: {ex.Message}");
                Disconnect();
            }
        }
    }
}
