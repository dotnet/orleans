using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OneBoxDeployment.IntegrationTests
{
    /// <summary>
    /// A handler to systematically test the presence of security headers in HTTP(S) responses.
    /// </summary>
    /// <remarks>This handler is used only in tests to catch security concerns.</remarks>
    public sealed class SecurityHeaderTestMessageHandler: DelegatingHandler
    {
        /// <summary>
        /// <see cref="DelegatingHandler.SendAsync(HttpRequestMessage, CancellationToken)"/>.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Trace.WriteLine(request.RequestUri.ToString());
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            //This handler is used only in tests, so if the call wasn't successful, the failure response
            //will flow as such back to the caller.
            if(!response.IsSuccessStatusCode)
            {
                return response;
            }

            const string ServerHeader = "server";
            if(response.Headers.Contains(ServerHeader))
            {
                throw new InvalidOperationException($"APIs should never return \"{ServerHeader}\" header.");
            }

            //The header and content type comparisons should be cased exactly like this, strictness here.
            //https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP.
            /*const string CspHeader = "Content-Security-Policy";
            const string CspReportUri = "report-uri /cspreport";
            if(!response.Headers.Contains(CspHeader) || !response.Headers.GetValues(CspHeader).Any(header => header.Contains(CspReportUri)))
            {
                throw new InvalidOperationException($"APIs should always define \"{CspHeader}\" header with contents of \"{CspReportUri}\".");
            }*/

            return response;
        }
    }
}
