
namespace Orleans.Hosting
{
    public class ServicePointOptions
    {
        public int DefaultConnectionLimit { get; set; } = DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT;
        public static readonly int DEFAULT_MIN_DOT_NET_CONNECTION_LIMIT = ThreadPoolOptions.DEFAULT_MIN_DOT_NET_THREAD_POOL_SIZE;

        public bool Expect100Continue { get; set; }

        public bool UseNagleAlgorithm { get; set; }
    }
}
