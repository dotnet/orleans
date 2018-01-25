using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Hosting
{
    public class ThreadPoolOptions
    {
        public int MinDotNetThreadPoolSize { get; set; } = DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;
        public const int DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE = 200;
    }
}
