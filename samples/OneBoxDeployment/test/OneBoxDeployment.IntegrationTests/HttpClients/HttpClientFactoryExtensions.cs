using System;
using System.Net.Http;

namespace OneBoxDeployment.IntegrationTests.HttpClients
{
    /// <summary>
    /// Extensions to <see cref="IHttpClientFactory"/>.
    /// </summary>
    public static class HttpClientFactoryExtensions
    {
        /// <summary>
        /// Creates a client of given type if registered.
        /// </summary>
        /// <typeparam name="TClient">The type of the client.</typeparam>
        /// <param name="clientFactory">The client factory.</param>
        /// <returns></returns>
        public static HttpClient CreateClient<TClient>(this IHttpClientFactory clientFactory)
        {
            if(clientFactory == null)
            {
                throw new ArgumentNullException(nameof(clientFactory));
            }

            return clientFactory.CreateClient(typeof(TClient).AssemblyQualifiedName);
        }
    }
}
