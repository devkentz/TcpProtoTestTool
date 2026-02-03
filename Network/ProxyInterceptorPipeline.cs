using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProtoTestTool.Controls;
using ProtoTestTool.ScriptContract;

namespace ProtoTestTool.Network
{
    public class ProxyInterceptorPipeline
    {
        private readonly List<InterceptorItem> _interceptors = new();

        public void Update(List<InterceptorItem> interceptors)
        {
            _interceptors.Clear();
            _interceptors.AddRange(interceptors);
        }

        public void Remove(InterceptorItem interceptor)
        {
            _interceptors.Remove(interceptor);
        }

        public void Clear()
        {
            _interceptors.Clear();
        }

        public async ValueTask InterceptorCall(PacketContext context)
        {
            await InterceptorCall(context, _interceptors);
        }

        private async Task InterceptorCall(PacketContext ctx, IEnumerable<InterceptorItem> interceptors)
        {
            foreach (var interceptorItem in interceptors)
            {
                var interceptor = (IPacketInterceptor) Activator.CreateInstance(interceptorItem.Type)!;

                if (ctx.Direction == PacketDirection.Outbound)
                    await interceptor.OnOutboundAsync(ctx);
                else
                    await interceptor.OnInboundAsync(ctx);
            }
        }
    }
}