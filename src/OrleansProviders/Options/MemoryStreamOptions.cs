namespace OrleansProviders.Options
{
    public class MemoryStreamOptions
    {
        public int MaxEventCount { get; set; } = DefaultMaxEventCount;

        public const int DefaultMaxEventCount = 16384;
    }
}