using Newtonsoft.Json;
using System.ComponentModel;

namespace OneBoxDeployment.Api.Dtos
{
    /// <summary>
    /// An envelope for a Content-Security Policy (CSP) violation.
    /// </summary>
    public sealed class CspReportRequest
    {
        /// <summary>
        /// An envelope for the the csp report.
        /// </summary>
        [JsonProperty(PropertyName = "csp-report")]
        [ReadOnly(false)]
        public CspReportDto CspReport { get; set; }
    }
}
