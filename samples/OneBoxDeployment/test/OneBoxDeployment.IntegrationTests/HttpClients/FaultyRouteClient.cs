using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneBoxDeployment.IntegrationTests.HttpClients
{
    /// <summary>
    /// A client to call specifically injected route that throws a fault.
    /// </summary>
    public class FaultyRouteClient
    {
        /// <summary>
        /// The HTTP client factory to use to call the API.
        /// </summary>
        private IHttpClientFactory ClientFactory { get; }


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="clientFactory">The <see cref="IHttpClientFactory"/> instance to use.</param>
        public FaultyRouteClient(IHttpClientFactory clientFactory)
        {
            ClientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }


        /// <summary>
        /// Call the faulty route as defined in <see cref="ConfigurationKeys.AlwaysFaultyRoute"/>.
        /// </summary>
        /// <param name="cancellation">The cancellation token.</param>
        /// <returns>The <see cref="HttpResponseMessage"/> for examination.</returns>
        public async Task<HttpResponseMessage> CallFaultyRouteAsync(CancellationToken cancellation = default)
        {
            using(var client = ClientFactory.CreateClient<FaultyRouteClient>())
            {
                return await client.GetAsync("/internalservererror", cancellation).ConfigureAwait(false);
            }
        }
    }
}
