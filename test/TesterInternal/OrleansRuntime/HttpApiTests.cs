using Orleans.TestingHost;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace UnitTests.HttpApiTests
{
    /// <summary>
    /// Test the Http API by making a call to it over HTTP
    /// </summary>
    public class HttpApiTests : OrleansTestingBase, IClassFixture<HttpApiTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(1);
                var nodeConfig = options.ClusterConfiguration.CreateNodeConfigurationForSilo("Primary");

                nodeConfig.HttpApiEnabled = true;
                nodeConfig.HttpApiPort = 9090;

                return new TestCluster(options);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task HttpApiTests_BasicHttpCall()
        {
            using (var client = new HttpClient())
            {
                using (var response = await client.GetAsync("http://localhost:9090/grain/UnitTests.GrainInterfaces.ISimpleGrain/0/SetA?a=42"))
                {
                    Assert.True(response.IsSuccessStatusCode);
                    var content = await response.Content.ReadAsStringAsync();
                    Assert.True(string.IsNullOrWhiteSpace(content));
                }

                using (var response = await client.GetAsync("http://localhost:9090/grain/UnitTests.GrainInterfaces.ISimpleGrain/0/GetA"))
                {
                    Assert.True(response.IsSuccessStatusCode);
                    var content = await response.Content.ReadAsStringAsync();
                    Assert.Equal("42", content);
                }

            }
        }

        [Fact, TestCategory("Functional")]
        public async Task HttpApiTests_Http404()
        {
            using (var client = new HttpClient())
            {
                using (var response = await client.GetAsync("http://localhost:9090/grain/UnitTests.GrainInterfaces.NO_GRAIN/0/SetA?a=42"))
                {
                    Assert.False(response.IsSuccessStatusCode);
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                }

                using (var response = await client.GetAsync("http://localhost:9090/grain/UnitTests.GrainInterfaces.ISimpleGrain/0/NO_METHOD"))
                {
                    Assert.False(response.IsSuccessStatusCode);
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                }

                using (var response = await client.GetAsync("http://localhost:9090/BAD_URL"))
                {
                    Assert.False(response.IsSuccessStatusCode);
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                }

            }
        }


    }
}
