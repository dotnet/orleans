using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Threading;

namespace Orleans.Runtime
{
    internal class ExecutorService
    {
        private readonly IServiceProvider serviceProvider;

        public ExecutorService(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public ThreadPoolExecutor GetExecutor(ThreadPoolExecutorOptions options)
        {
            return ActivatorUtilities.CreateInstance<ThreadPoolExecutor>(this.serviceProvider, options);
        }
    }
}
