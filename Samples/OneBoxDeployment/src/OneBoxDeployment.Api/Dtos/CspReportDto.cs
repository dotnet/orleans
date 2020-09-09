using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace OneBoxDeployment.Api.Dtos
{
    /// <summary>
    /// The contents of a Content-Security-Policy (CSP) violation report.
    /// </summary>
    [DebuggerDisplay("DocumentUri = {DocumentUri, StatusCode = {StatusCode}")]
    public sealed class CspReportDto
    {
        /// <summary>
        /// The CSP document URI.
        /// </summary>
        /// <remarks>See more at <a href="https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP">CSP</a>.</remarks>
        [JsonPropertyName("document-uri")]
        [ReadOnly(false)]
        public string DocumentUri { get; set; }

        /// <summary>
        /// The CSP referrer.
        /// </summary>
        /// <remarks>See more at <a href="https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Security-Policy/referrer">Referrer</a>.</remarks>
        [JsonPropertyName("referrer")]
        [ReadOnly(false)]
        public string Referrer { get; set; }

        /// <summary>
        /// The CSP violated directive.
        /// </summary>
        /// <remarks>See more at <a href="https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP">CSP</a>.</remarks>
        [JsonPropertyName("violated-directive")]
        [ReadOnly(false)]
        public string ViolatedDirective { get; set; }

        /// <summary>
        /// The CSP effective directive.
        /// </summary>
        /// <remarks>See more at <a href="https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP">CSP</a>.</remarks>
        [JsonPropertyName("effective-directive")]
        [ReadOnly(false)]
        public string EffectiveDirective { get; set; }

        /// <summary>
        /// The CSP original policy.
        /// </summary>
        /// <remarks>See more at <a href="https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP">CSP</a>.</remarks>
        [JsonPropertyName("original-policy")]
        [ReadOnly(false)]
        public string OriginalPolicy { get; set; }

        /// <summary>
        /// The CSP blocked URI.
        /// </summary>
        /// <remarks>See more at <a href="https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP">CSP</a>.</remarks>
        [JsonPropertyName("blocked-uri")]
        [ReadOnly(false)]
        public string BlockedUri { get; set; }

        /// <summary>
        /// The CSP status code.
        /// </summary>
        /// <remarks>See more at <a href="https://developer.mozilla.org/en-US/docs/Web/HTTP/CSP">CSP</a>.</remarks>
        [JsonPropertyName("status-code")]
        [ReadOnly(false)]
        public int StatusCode { get; set; }
    }
}
