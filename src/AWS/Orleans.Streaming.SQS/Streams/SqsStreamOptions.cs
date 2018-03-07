
namespace Orleans.Configuration
{
    public class SqsOptions
    {
        public string ClusterId { get; set; }

        [Redact]
        public string ConnectionString { get; set; }
    }
}
