#if !NETSTANDARD_TODO
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Consul.Tests
{
    public static class ConsulTestUtils
    {
        public const string CONSUL_ENDPOINT = "http://localhost:8500";
        private static readonly Lazy<bool> EnsureConsulLazy = new Lazy<bool>(() => EnsureConsulAsync().Result);

        public static void EnsureConsul()
        {
            if (!EnsureConsulLazy.Value)
                throw new SkipException("Consul cluster isn't running");
        }

        public  static async Task<bool> EnsureConsulAsync()
        {
            try
            {
                var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                var response = await client.GetAsync($"{CONSUL_ENDPOINT}/v1/health/service/consul?pretty");
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

#endif