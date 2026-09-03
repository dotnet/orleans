using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Threading;

namespace Orleans.Connections.Security
{
    /// <summary>
    /// Provides access to certificate information for an authenticated TLS connection.
    /// </summary>
    public interface ITlsConnectionFeature
    {
        /// <summary>
        /// Synchronously retrieves the remote endpoint's certificate, if any.
        /// </summary>
        X509Certificate2? RemoteCertificate { get; set; }

        /// <summary>
        /// Asynchronously retrieves the remote endpoint's certificate, if any.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token associated with the request.</param>
        /// <returns>A task which returns the remote endpoint's certificate, or <see langword="null"/> if none is available.</returns>
        Task<X509Certificate2?> GetRemoteCertificateAsync(CancellationToken cancellationToken);
    }
}
