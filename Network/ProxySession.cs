using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NetCoreServer;
using ProtoTestTool.ScriptContract;
using ProtoTestTool.Services;

namespace ProtoTestTool.Network
{
    public class ProxySession : TcpSession
    {
        private readonly UpstreamClient _upstream;
        private readonly ProxyInterceptorPipeline _pipeline;
        private readonly IPacketCodec _codec;

        private readonly Channel<ReadOnlyMemory<byte>> _outboundChannel =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });

        private readonly Channel<ReadOnlyMemory<byte>> _inboundChannel =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });

        public ProxySession(TcpServer server, string upstreamIp, int upstreamPort, ProxyInterceptorPipeline pipeline, IPacketCodec codec)
            : base(server)
        {
            _pipeline = pipeline;
            _codec = codec;
            _upstream = new UpstreamClient(upstreamIp, upstreamPort, this);
        }

        protected override void OnConnected()
        {
            _upstream.ConnectAsync();
            _ = ConsumeLoopAsync(_outboundChannel, PacketDirection.Outbound);
            _ = ConsumeLoopAsync(_inboundChannel, PacketDirection.Inbound);
        }

        protected override void OnDisconnected()
        {
            _outboundChannel.Writer.TryComplete();
            _inboundChannel.Writer.TryComplete();
            _upstream.DisconnectAsync();
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            var copy = buffer.AsSpan((int)offset, (int)size).ToArray();
            _outboundChannel.Writer.TryWrite(copy);
        }

        public void OnUpstreamReceived(byte[] buffer, long offset, long size)
        {
            var copy = buffer.AsSpan((int)offset, (int)size).ToArray();
            _inboundChannel.Writer.TryWrite(copy);
        }

        private async Task ConsumeLoopAsync(Channel<ReadOnlyMemory<byte>> channel, PacketDirection direction)
        {
            var accumulator = new ByteBuffer();

            try
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync())
                {
                    accumulator.Append(chunk.Span);

                    while (accumulator.Length > 0)
                    {
                        var span = accumulator.WrittenSpan;
                        var consumed = _codec.TryDecode(ref span, out var packet);
                        if (consumed <= 0)
                            break;
                        
                        Debug.Assert(packet != null);

                        var rawMemory = new ReadOnlyMemory<byte>(accumulator.WrittenSpan[..consumed].ToArray());
                        accumulator.Consume(consumed);

                        var context = new ProxyPacketContext(packet, direction, rawMemory);

                        if (PacketRecorder.Proxy.IsRecording)
                        {
                            PacketRecorder.Proxy.Record(direction, packet);
                        }

                        if (direction == PacketDirection.Inbound)
                            await _pipeline.RunInboundAsync(context);
                        else
                            await _pipeline.RunOutboundAsync(context);

                        if (context.Drop)
                            continue;

                        var dataToSend = context.Bypass && context.Raw.Length > 0
                            ? context.Raw
                            : _codec.Encode(context.Packet);

                        if (direction == PacketDirection.Outbound)
                            _upstream.SendAsync(GetArray(dataToSend));
                        else
                            SendAsync(GetArray(dataToSend));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProxySession] Error ({direction}): {ex.Message}");
                Disconnect();
            }
        }

        private static byte[] GetArray(ReadOnlyMemory<byte> memory)
        {
            return MemoryMarshal.TryGetArray(memory, out var segment)
                   && segment.Offset == 0
                   && segment.Count == segment.Array!.Length
                ? segment.Array
                : memory.ToArray();
        }

        /// <summary>
        /// Contiguous byte accumulator with compaction.
        /// Replaces List&lt;byte&gt; to avoid per-element Add and O(n) RemoveRange.
        /// </summary>
        private sealed class ByteBuffer
        {
            private byte[] _buffer = new byte[4096];
            private int _start;
            private int _end;

            public int Length => _end - _start;

            public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(_start, Length);

            public void Append(ReadOnlySpan<byte> data)
            {
                EnsureCapacity(data.Length);
                data.CopyTo(_buffer.AsSpan(_end));
                _end += data.Length;
            }

            public void Consume(int count)
            {
                _start += count;

                if (_start == _end)
                {
                    _start = 0;
                    _end = 0;
                }
                else if (_start > _buffer.Length / 2)
                {
                    Compact();
                }
            }

            private void EnsureCapacity(int additionalBytes)
            {
                if (_buffer.Length - _end >= additionalBytes)
                    return;

                if (_buffer.Length - Length >= additionalBytes)
                {
                    Compact();
                    return;
                }

                var newSize = Math.Max(_buffer.Length * 2, Length + additionalBytes);
                var newBuf = new byte[newSize];
                System.Buffer.BlockCopy(_buffer, _start, newBuf, 0, Length);
                _end = Length;
                _start = 0;
                _buffer = newBuf;
            }

            private void Compact()
            {
                var len = Length;
                System.Buffer.BlockCopy(_buffer, _start, _buffer, 0, len);
                _start = 0;
                _end = len;
            }
        }
    }
}
