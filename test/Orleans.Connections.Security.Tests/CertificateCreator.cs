using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

namespace Orleans.Connections.Security.Tests
{
    internal static class TestCertificateHelper
    {
        // See http://oid-info.com/get/1.3.6.1.5.5.7.3.1
        // Indicates that a certificate can be used as a TLS server certificate
        public const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

        // See http://oid-info.com/get/1.3.6.1.5.5.7.3.2
        // Indicates that a certificate can be used as a TLS client certificate
        public const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

        private const X509KeyUsageFlags KeyUsageFlags = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyAgreement;

        public static X509Certificate2 CreateSelfSignedCertificate(string subjectName, string[] extendedKeyUsageOids = null)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));

            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            request.CertificateExtensions.Add(new X509KeyUsageExtension(KeyUsageFlags, false));

            if (extendedKeyUsageOids != null && extendedKeyUsageOids.Length > 0)
            {
                var extendedKeyUsages = new OidCollection();
                foreach (var oid in extendedKeyUsageOids)
                {
                    extendedKeyUsages.Add(new Oid(oid));
                }
                var extension = new X509EnhancedKeyUsageExtension(extendedKeyUsages, false);
                request.CertificateExtensions.Add(extension);
            }

            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(10)), DateTimeOffset.UtcNow.AddYears(5));

            return certificate;
        }

        public static string ConvertToBase64(X509Certificate2 certificate)
        {
            return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "testing-only"));
        }

        public static X509Certificate2 ConvertFromBase64(string encodedCertificate)
        {
            var rawData = Convert.FromBase64String(encodedCertificate);
            return new X509Certificate2(rawData, "testing-only");
        }
    }
}
