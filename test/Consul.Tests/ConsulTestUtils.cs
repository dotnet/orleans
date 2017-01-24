#if !NETSTANDARD_TODO
using System.Net;
using System.Net.Http;
using Xunit;

namespace Consul.Tests
{
    public static class ConsulTestUtils
    {
        public const string CONSUL_ENDPOINT = "http://localhost:8500";

        public static void EnsureConsul()
        {
            var client = new HttpClient();
            var response = client.GetAsync($"{CONSUL_ENDPOINT}/v1/health/service/consul?pretty").Result;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new SkipException("Consul cluster isn't running");
        }
    }
}

#endif