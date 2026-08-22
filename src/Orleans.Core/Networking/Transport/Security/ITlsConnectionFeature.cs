#nullable enable

using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Provides access to TLS connection certificate information.
/// </summary>
public interface ITlsConnectionFeature
{
    /// <summary>
    /// Gets or sets the remote endpoint's certificate, if any.
    /// </summary>
    X509Certificate2? RemoteCertificate { get; set; }

    /// <summary>
    /// Asynchronously retrieves the remote endpoint's certificate, if any.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The remote endpoint certificate, or <see langword="null"/> if none is available.</returns>
    Task<X509Certificate2?> GetRemoteCertificateAsync(CancellationToken cancellationToken);
}
