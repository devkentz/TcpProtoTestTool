using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool.Network
{
    public class ProxyInterceptorPipeline
    {
        private readonly List<IProxyPacketInterceptor> _interceptors = new();
        private readonly object _lock = new();

        public void Add(IProxyPacketInterceptor interceptor)
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

        public async ValueTask RunInboundAsync(ProxyPacketContext context)
        {
            List<IProxyPacketInterceptor> snapshot;
            lock (_lock)
            {
                snapshot = new List<IProxyPacketInterceptor>(_interceptors);
            }

            foreach (var interceptor in snapshot)
            {
                if (context.Drop) return;
                await interceptor.OnInboundAsync(context);
            }
        }

        public async ValueTask RunOutboundAsync(ProxyPacketContext context)
        {
            List<IProxyPacketInterceptor> snapshot;
            lock (_lock)
            {
                snapshot = new List<IProxyPacketInterceptor>(_interceptors);
            }

            foreach (var interceptor in snapshot)
            {
                if (context.Drop) return;
                await interceptor.OnOutboundAsync(context);
            }
        }
    }
}
