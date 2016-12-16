using Orleans.TestingHost;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace UnitTests.HttpApiTests
{
    /// <summary>
    /// Test the Http API by making a call to it over HTTP
    /// </summary>
    public class HttpApiWithAuthTests : OrleansTestingBase, IClassFixture<HttpApiWithAuthTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(1);
                var nodeConfig = options.ClusterConfiguration.CreateNodeConfigurationForSilo("Primary");

                nodeConfig.HttpApiEnabled = true;
                nodeConfig.HttpApiUsername = "orleansuser";
                nodeConfig.HttpApiPassword = "orleanspassword";

                return new TestCluster(options);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task HttpApiTests_HttpCallWithAuth()
        {
            using (var client = new HttpClient())
            {
                
                using (var response = await client.GetAsync("http://localhost:8080/grain/UnitTests.GrainInterfaces.ISimpleGrain/0/SetA?a=42"))
                {
                    Assert.False(response.IsSuccessStatusCode);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }

                var headerValue = Convert.ToBase64String(Encoding.ASCII.GetBytes("orleansuser:orleanspassword"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", headerValue);

                using (var response = await client.GetAsync("http://localhost:8080/grain/UnitTests.GrainInterfaces.ISimpleGrain/0/SetA?a=42"))
                {
                    Assert.True(response.IsSuccessStatusCode);
                }

            }
        }


    }
}
