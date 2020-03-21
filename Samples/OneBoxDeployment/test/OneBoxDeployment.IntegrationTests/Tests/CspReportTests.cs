using OneBoxDeployment.Api.Dtos;
using OneBoxDeployment.IntegrationTests.HttpClients;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Xunit.Extensions.Ordering;

namespace OneBoxDeployment.IntegrationTests.Tests
{
    /// <summary>
    /// Tests reading and writing Content-Security-Policy (CSP) reports.
    /// </summary>
    public sealed class CspReportTests: IAssemblyFixture<IntegrationTestFixture>
    {

        /// <summary>
        /// The preconfigured client to call the specifically constructed, faulty route.
        /// </summary>
        private CspClient CspClient { get; }


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="fixture">The fixture that holds the testing setup.</param>
        public CspReportTests(IntegrationTestFixture fixture)
        {
            CspClient = fixture.ServicesProvider.GetService<CspClient>();
        }


        /// <summary>
        /// Tries to report a CSP violation.
        /// </summary>
        [Fact]
        public async Task ReportCspViolation()
        {
            var insertedCspReportRaw = await CspClient.ReportAsync(new CspReportRequest
            {
                CspReport = new CspReportDto
                {
                    DocumentUri = nameof(CspReportDto.DocumentUri),
                    Referrer = nameof(CspReportDto.Referrer),
                    ViolatedDirective = nameof(CspReportDto.Referrer),
                    EffectiveDirective = nameof(CspReportDto.EffectiveDirective),
                    OriginalPolicy = nameof(CspReportDto.OriginalPolicy),
                    BlockedUri = nameof(CspReportDto.BlockedUri),
                    StatusCode = 0
                }
            }).ConfigureAwait(false);

            Assert.Equal(HttpStatusCode.OK, insertedCspReportRaw.StatusCode);
        }
    }
}
