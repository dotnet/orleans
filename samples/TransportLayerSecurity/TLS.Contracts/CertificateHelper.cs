using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HelloWorld;

/// <summary>
/// Helper class for managing self-signed certificates for TLS communication.
/// </summary>
public static class CertificateHelper
{
    public const string CertificateSubject = "fakedomain.faketld";
    public const StoreName StoreName = System.Security.Cryptography.X509Certificates.StoreName.My;
    public const StoreLocation StoreLocation = System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser;

    /// <summary>
    /// Ensures a self-signed certificate exists in the certificate store.
    /// If the certificate doesn't exist, it creates one.
    /// </summary>
    /// <returns>The certificate that was found or created.</returns>
    public static X509Certificate2 EnsureCertificateExists()
    {
        var existingCert = FindCertificate();
        if (existingCert is not null)
        {
            Console.WriteLine($"Found existing certificate: {CertificateSubject}");
            return existingCert;
        }

        Console.WriteLine($"Certificate '{CertificateSubject}' not found. Creating a new self-signed certificate...");
        return CreateAndStoreCertificate();
    }

    /// <summary>
    /// Finds an existing certificate in the store.
    /// </summary>
    public static X509Certificate2? FindCertificate()
    {
        using var store = new X509Store(StoreName, StoreLocation);
        store.Open(OpenFlags.ReadOnly);

        var certs = store.Certificates.Find(
            X509FindType.FindBySubjectName,
            CertificateSubject,
            validOnly: false);

        // Return a valid (not expired) certificate if available
        foreach (var cert in certs)
        {
            if (cert.NotAfter > DateTime.Now && cert.NotBefore <= DateTime.Now)
            {
                return cert;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a self-signed certificate and stores it in the certificate store.
    /// </summary>
    private static X509Certificate2 CreateAndStoreCertificate()
    {
        // Create RSA key
        using var rsa = RSA.Create(2048);

        // Create certificate request
        var request = new CertificateRequest(
            $"CN={CertificateSubject}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Add extensions
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [
                    new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
                    new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
                ],
                false));

        // Add Subject Alternative Name
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(CertificateSubject);
        request.CertificateExtensions.Add(sanBuilder.Build());

        // Create certificate (valid for 1 year)
        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore.AddYears(1);

        var certificate = request.CreateSelfSigned(notBefore, notAfter);

        // Export and re-import with exportable private key for storage
#if NET9_0_OR_GREATER
        var certWithKey = X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#else
        var certWithKey = new X509Certificate2(
            certificate.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
#endif

        // Store in certificate store
        using var store = new X509Store(StoreName, StoreLocation);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certWithKey);
        store.Close();

        Console.WriteLine($"Created and stored self-signed certificate: {CertificateSubject}");
        Console.WriteLine($"  Thumbprint: {certWithKey.Thumbprint}");
        Console.WriteLine($"  Valid until: {certWithKey.NotAfter:yyyy-MM-dd}");

        return certWithKey;
    }

    /// <summary>
    /// Removes the self-signed certificate from the store (for cleanup).
    /// </summary>
    public static void RemoveCertificate()
    {
        using var store = new X509Store(StoreName, StoreLocation);
        store.Open(OpenFlags.ReadWrite);

        var certs = store.Certificates.Find(
            X509FindType.FindBySubjectName,
            CertificateSubject,
            validOnly: false);

        foreach (var cert in certs)
        {
            Console.WriteLine($"Removing certificate: {cert.Thumbprint}");
            store.Remove(cert);
        }

        store.Close();
    }
}
