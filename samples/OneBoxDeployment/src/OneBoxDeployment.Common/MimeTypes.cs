namespace OneBoxDeployment.Common
{
    /// <summary>
    /// MIME types that are not defined in .NET libraries.
    /// </summary>
    public static class MimeTypes
    {
        /// <summary>
        /// A problem detail MIME type. See more at <a href="https://tools.ietf.org/html/rfc7807">RFC 7809</a>.
        /// </summary>
        public const string ProblemDetailJsonMimeType = "application/problem+json";

        /// <summary>
        /// A Content Security Policy (CSP) report MIME type.
        /// </summary>
        public const string MimeTypeCspReport = "application/csp-report";
    }
}
