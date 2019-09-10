using System.Net.Http;
using System.Text;
using Newtonsoft.Json;

namespace OneBoxDeployment.IntegrationTests.Dtos
{
    /// <summary>
    /// Translates the contents to JSON in <see cref="Encoding.UTF8"/> and <em>Content-Type: application/csp-report</em>.
    /// </summary>
    public sealed class CspReportContent: StringContent
    {
        public CspReportContent(object obj): base(JsonConvert.SerializeObject(obj), Encoding.UTF8, "application/csp-report") { }
    }
}
