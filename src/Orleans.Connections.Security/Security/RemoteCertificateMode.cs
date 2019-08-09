namespace Orleans.Connections.Security
{
    /// <summary>
    /// Describes the remote certificate requirements for a TLS connection.
    /// </summary>
    public enum RemoteCertificateMode
    {
        /// <summary>
        /// A remote certificate is not required and will not be requested from remote endpoints.
        /// </summary>
        NoCertificate,

        /// <summary>
        /// A remote certificate will be requested; however, authentication will not fail if a certificate is not provided by the remote endpoint.
        /// </summary>
        AllowCertificate,

        /// <summary>
        /// A remote certificate will be requested, and the remote endpoint must provide a valid certificate for authentication.
        /// </summary>
        RequireCertificate
    }
}
