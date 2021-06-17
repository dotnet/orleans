using OneBoxDeployment.Common;
using OneBoxDeployment.IntegrationTests.HttpClients;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Xunit.Extensions.Ordering;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;

namespace OneBoxDeployment.IntegrationTests.Tests
{
    /// <summary>
    /// Tests for fault injected cases.
    /// </summary>
    public sealed class FaultInjectionTests: IAssemblyFixture<IntegrationTestFixture>
    {
        /// <summary>
        /// The preconfigured client to call the specifically constructed, faulty route.
        /// </summary>
        private FaultyRouteClient FaultClientClient { get; }


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="fixture">The fixture that holds the testing setup.</param>
        public FaultInjectionTests(IntegrationTestFixture fixture)
        {
            var sp = fixture.ServicesProvider;
            FaultClientClient = sp.GetService<FaultyRouteClient>();
        }


        /// <summary>
        /// Tests global exception handler catches errors by calling a specifically injected faulty route.
        /// </summary>
        [Fact]
        public async Task DataApiUnhandledExceptionsReturnHttp500WithJsonContent()
        {
            var supposedlyFaultyRouteValue = await FaultClientClient.CallFaultyRouteAsync().ConfigureAwait(false);

            Assert.Equal(HttpStatusCode.InternalServerError, supposedlyFaultyRouteValue.StatusCode);
            Assert.Equal(MimeTypes.ProblemDetailJsonMimeType, supposedlyFaultyRouteValue.Content.Headers.ContentType.MediaType);

            var response = await supposedlyFaultyRouteValue.Content.ReadFromJsonAsync<ProblemDetails>().ConfigureAwait(false);
            Assert.Equal((int)HttpStatusCode.InternalServerError, response.Status);
            Assert.Equal("Internal Server Error", response.Title);
            Assert.StartsWith("urn:oneboxdeployment:error", response.Instance);
        }
    }
}
