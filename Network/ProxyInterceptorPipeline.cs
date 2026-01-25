using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool.Network
{
    public class ProxyInterceptorPipeline
    {
        private readonly List<IProxyPacketInterceptor> _interceptors = new();

        public void Add(IProxyPacketInterceptor interceptor)
        {
            _interceptors.Add(interceptor);
        }

        public void Clear()
        {
            _interceptors.Clear();
        }

        public async ValueTask RunInboundAsync(ProxyPacketContext context)
        {
            foreach (var interceptor in _interceptors)
            {
                if (context.Drop) return; // Stop processing if dropped
                await interceptor.OnInboundAsync(context);
            }
        }

        public async ValueTask RunOutboundAsync(ProxyPacketContext context)
        {
            foreach (var interceptor in _interceptors)
            {
                if (context.Drop) return; // Stop processing if dropped
                await interceptor.OnOutboundAsync(context);
            }
        }
    }
}
