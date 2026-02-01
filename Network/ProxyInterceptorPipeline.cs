using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool.Network
{
    public class ProxyInterceptorPipeline
    {
        private readonly List<IPacketInterceptor> _interceptors = new();
        private readonly object _lock = new();

        public void Add(IPacketInterceptor interceptor)
        {
            lock (_lock)
            {
                _interceptors.Add(interceptor);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _interceptors.Clear();
            }
        }

        public async ValueTask RunInboundAsync(PacketContext context)
        {
            List<IPacketInterceptor> snapshot;
            lock (_lock)
            {
                snapshot = new List<IPacketInterceptor>(_interceptors);
            }

            foreach (var interceptor in snapshot)
            {
                if (context.Drop) return;
                await interceptor.OnInboundAsync(context);
            }
        }

        public async ValueTask RunOutboundAsync(PacketContext context)
        {
            List<IPacketInterceptor> snapshot;
            lock (_lock)
            {
                snapshot = new List<IPacketInterceptor>(_interceptors);
            }

            foreach (var interceptor in snapshot)
            {
                if (context.Drop) return;
                await interceptor.OnOutboundAsync(context);
            }
        }
    }
}
