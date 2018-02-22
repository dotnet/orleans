using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    public class PubSubStreamOptions : PersistentStreamOptions
    {
        public string ProjectId { get; set; }

        public string TopicId { get; set; }

        public string ClusterId { get; set; }

        public string CustomEndpoint { get; set; }

        public int CacheSize { get; set; } = CACHE_SIZE_DEFAULT;
        public const int CACHE_SIZE_DEFAULT = 4096;

        public int NumSubscriptions { get; set; } = NUMBER_SUBSCRIPTIONS_DEFAULT;
        public const int NUMBER_SUBSCRIPTIONS_DEFAULT = 8;

        private TimeSpan? deadline;
        public TimeSpan? Deadline
        {
            get { return this.deadline; }
            set { this.deadline = (value.HasValue) ? TimeSpan.FromTicks(Math.Min(value.Value.Ticks, MAX_DEADLINE.Ticks)) : value; }
        }
        public static readonly TimeSpan MAX_DEADLINE = TimeSpan.FromSeconds(600);
    }
}
