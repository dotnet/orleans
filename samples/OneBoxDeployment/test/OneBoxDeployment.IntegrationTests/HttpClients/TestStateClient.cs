using OneBoxDeployment.Api.Dtos;
using OneBoxDeployment.IntegrationTests.Dtos;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneBoxDeployment.IntegrationTests.HttpClients
{
    /// <summary>
    /// A client to call specifically an Orleans state client.
    /// </summary>
    public class TestStateClient
    {
        /// <summary>
        /// The HTTP client factory to use to call the API.
        /// </summary>
        private IHttpClientFactory ClientFactory { get; }

        /// <summary>
        /// The path fragment to the Orleans stateful grain testing endpoint.
        /// </summary>
        public static string TestStatePathFragment = "api/OneBoxDeployment/increment";


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="clientFactory">The <see cref="IHttpClientFactory"/> instance to use.</param>
        public TestStateClient(IHttpClientFactory clientFactory)
        {
            ClientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }


        /// <summary>
        /// Posts a <see cref="CspReportRequest"/> to the configred API.
        /// </summary>
        /// <param name="cancellation">The cancellation token.</param>
        /// <returns>The <see cref="HttpResponseMessage"/> for examination.</returns>
        public async Task<HttpResponseMessage> IncrementByAsync(int grainId, int incrementBy, CancellationToken cancellation = default)
        {
            using(var client = ClientFactory.CreateClient<TestStateClient>())
            {
                return await client.PostAsync(
                    TestStatePathFragment,
                    new JsonContent(new Increment
                    {
                        GrainId = grainId,
                        IncrementBy = incrementBy
                    }), cancellation).ConfigureAwait(false);
            }
        }
    }
}
