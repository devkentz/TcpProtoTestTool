using System;

namespace ProtoTestTool.Network
{
    /// <summary>
    /// Contiguous byte accumulator with compaction.
    /// Avoids per-element Add and O(n) RemoveRange of List&lt;byte&gt;.
    /// </summary>
    public sealed class ByteBuffer
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

        public void Clear()
        {
            _start = 0;
            _end = 0;
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
            Buffer.BlockCopy(_buffer, _start, newBuf, 0, Length);
            _end = Length;
            _start = 0;
            _buffer = newBuf;
        }

        private void Compact()
        {
            var len = Length;
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, len);
            _start = 0;
            _end = len;
        }
    }
}
