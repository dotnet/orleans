using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration
{
    public class SimpleQueueCacheOptions
    {
        public int CacheSize { get; set; } = DEFAULT_CACHE_SIZE;
        public const int DEFAULT_CACHE_SIZE = 4096;
    }
}
