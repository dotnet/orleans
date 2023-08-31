using System;

namespace Orleans.Configuration
{
    public class PubSubOptions
    {
        public string ProjectId { get; set; }

        public string TopicId { get; set; }

        public string CustomEndpoint { get; set; }

        private TimeSpan? deadline;
        public TimeSpan? Deadline
        {
            get { return deadline; }
            set { deadline = (value.HasValue) ? TimeSpan.FromTicks(Math.Min(value.Value.Ticks, MAX_DEADLINE.Ticks)) : value; }
        }
        public static readonly TimeSpan MAX_DEADLINE = TimeSpan.FromSeconds(600);
    }
}
