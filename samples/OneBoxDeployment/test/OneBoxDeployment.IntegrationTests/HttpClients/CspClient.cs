using OneBoxDeployment.Api.Dtos;
using OneBoxDeployment.IntegrationTests.Dtos;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneBoxDeployment.IntegrationTests.HttpClients
{
    /// <summary>
    /// A client to call a CSP endpoint.
    /// </summary>
    public class CspClient
    {
        /// <summary>
        /// The HTTP client factory to use to call the API.
        /// </summary>
        private IHttpClientFactory ClientFactory { get; }

        /// <summary>
        /// The path fragment to the CSP report endpoint.
        /// </summary>
        public static string CspPathFragment = "cspreport";


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="clientFactory">The <see cref="IHttpClientFactory"/> instance to use.</param>
        public CspClient(IHttpClientFactory clientFactory)
        {
            ClientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }


        /// <summary>
        /// Posts a <see cref="CspReportRequest"/> to the configred API.
        /// </summary>
        /// <param name="cancellation">The cancellation token.</param>
        /// <returns>The <see cref="HttpResponseMessage"/> for examination.</returns>
        public async Task<HttpResponseMessage> ReportAsync(CspReportRequest cspReport, CancellationToken cancellation = default)
        {
            using(var client = ClientFactory.CreateClient<CspClient>())
            {
                return await client.PostAsync(CspPathFragment, new CspReportContent(cspReport), cancellation).ConfigureAwait(false);
            }
        }
    }
}
