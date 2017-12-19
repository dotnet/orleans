using Orleans.Threading;

namespace Orleans.Runtime
{
    internal class ExecutorService
    {
        public ThreadPoolExecutor GetExecutor(ThreadPoolExecutorOptions options)
        {
            return new ThreadPoolExecutor(options);
        }
    }
}
