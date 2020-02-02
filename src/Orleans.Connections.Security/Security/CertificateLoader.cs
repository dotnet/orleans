using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Security
{
    public static class CertificateLoader
    {
        // See http://oid-info.com/get/1.3.6.1.5.5.7.3.1
        // Indicates that a certificate can be used as a TLS server certificate
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

        // See http://oid-info.com/get/1.3.6.1.5.5.7.3.2
        // Indicates that a certificate can be used as a TLS client certificate
        private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

        public static X509Certificate2 LoadFromStoreCert(string subject, string storeName, StoreLocation storeLocation, bool allowInvalid, bool server)
        {
            using (var store = new X509Store(storeName, storeLocation))
            {
                X509Certificate2Collection storeCertificates = null;
                X509Certificate2 foundCertificate = null;

                try
                {
                    store.Open(OpenFlags.ReadOnly);
                    storeCertificates = store.Certificates;
                    var foundCertificates = storeCertificates.Find(X509FindType.FindBySubjectName, subject, !allowInvalid);
                    foundCertificate = foundCertificates
                        .OfType<X509Certificate2>()
                        .Where(c => server ? IsCertificateAllowedForServerAuth(c) : IsCertificateAllowedForClientAuth(c))
                        .Where(DoesCertificateHaveAnAccessiblePrivateKey)
                        .OrderByDescending(certificate => certificate.NotAfter)
                        .FirstOrDefault();

                    if (foundCertificate == null)
                    {
                        throw new InvalidOperationException($"Certificate {subject} not found in store {storeLocation} / {storeName}. AllowInvalid: {allowInvalid}");
                    }

                    return foundCertificate;
                }
                finally
                {
                    DisposeCertificates(storeCertificates, except: foundCertificate);
                }
            }
        }

        internal static bool IsCertificateAllowedForServerAuth(X509Certificate2 certificate) => IsCertificateAllowedForKeyUsage(certificate, ServerAuthenticationOid);

        internal static bool IsCertificateAllowedForClientAuth(X509Certificate2 certificate) => IsCertificateAllowedForKeyUsage(certificate, ClientAuthenticationOid);

        private static bool IsCertificateAllowedForKeyUsage(X509Certificate2 certificate, string purposeOid)
        {
            /* If the Extended Key Usage extension is included, then we check that the serverAuth usage is included. (http://oid-info.com/get/1.3.6.1.5.5.7.3.1)
             * If the Extended Key Usage extension is not included, then we assume the certificate is allowed for all usages.
             *
             * See also https://blogs.msdn.microsoft.com/kaushal/2012/02/17/client-certificates-vs-server-certificates/
             *
             * From https://tools.ietf.org/html/rfc3280#section-4.2.1.13 "Certificate Extensions: Extended Key Usage"
             *
             * If the (Extended Key Usage) extension is present, then the certificate MUST only be used
             * for one of the purposes indicated.  If multiple purposes are
             * indicated the application need not recognize all purposes indicated,
             * as long as the intended purpose is present.  Certificate using
             * applications MAY require that a particular purpose be indicated in
             * order for the certificate to be acceptable to that application.
             */

            var hasEkuExtension = false;

            foreach (var extension in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            {
                hasEkuExtension = true;
                foreach (var oid in extension.EnhancedKeyUsages)
                {
                    if (oid.Value.Equals(purposeOid, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return !hasEkuExtension;
        }

        internal static bool DoesCertificateHaveAnAccessiblePrivateKey(X509Certificate2 certificate)
            => certificate.HasPrivateKey;

        private static void DisposeCertificates(X509Certificate2Collection certificates, X509Certificate2 except)
        {
            if (certificates != null)
            {
                foreach (var certificate in certificates)
                {
                    if (!certificate.Equals(except))
                    {
                        certificate.Dispose();
                    }
                }
            }
        }
    }
}
