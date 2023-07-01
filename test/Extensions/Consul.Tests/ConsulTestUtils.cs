using System.Net;
using TestExtensions;
using Xunit;

namespace Consul.Tests
{
    public static class ConsulTestUtils
    {
        public static readonly string ConsulConnectionString = TestDefaultConfiguration.ConsulConnectionString;
        private static readonly Lazy<bool> EnsureConsulLazy = new Lazy<bool>(() => EnsureConsulAsync().Result);

        public static void EnsureConsul()
        {
            if (!EnsureConsulLazy.Value)
                throw new SkipException("Consul cluster isn't running");
        }

        public static async Task<bool> EnsureConsulAsync()
        {
            if (string.IsNullOrWhiteSpace(ConsulConnectionString))
            {
                return false;
            }

            try
            {
                var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                var response = await client.GetAsync($"{ConsulConnectionString}/v1/health/service/consul?pretty");
                return response.StatusCode == HttpStatusCode.OK;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
