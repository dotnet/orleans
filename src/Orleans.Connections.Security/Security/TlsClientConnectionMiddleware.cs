using System;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;

namespace Orleans.Connections.Security
{
    internal class TlsClientConnectionMiddleware
    {
        private readonly ConnectionDelegate _next;
        private readonly TlsOptions _options;
        private readonly ILogger _logger;
        private readonly X509Certificate2 _certificate;
        private readonly Func<object, string, X509CertificateCollection, X509Certificate, string[], X509Certificate2> _certificateSelector;

        public TlsClientConnectionMiddleware(ConnectionDelegate next, TlsOptions options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _next = next;

            // capture the certificate now so it can't be switched after validation
            _certificate = ValidateCertificate(options.LocalCertificate, options.ClientCertificateMode);
            _certificateSelector = options.LocalClientCertificateSelector;


            _options = options;
            _logger = loggerFactory?.CreateLogger<TlsClientConnectionMiddleware>();
        }

        public Task OnConnectionAsync(ConnectionContext context)
        {
            return InnerOnConnectionAsync(context);
        }

        private async Task InnerOnConnectionAsync(ConnectionContext context)
        {
            var feature = new TlsConnectionFeature();
            context.Features.Set<ITlsConnectionFeature>(feature);
            context.Features.Set<ITlsHandshakeFeature>(feature);

            var memoryPool = context.Features.Get<IMemoryPoolFeature>()?.MemoryPool;

            var inputPipeOptions = new StreamPipeReaderOptions
            (
                pool: memoryPool,
                bufferSize: memoryPool.GetMinimumSegmentSize(),
                minimumReadSize: memoryPool.GetMinimumAllocSize(),
                leaveOpen: true
            );

            var outputPipeOptions = new StreamPipeWriterOptions
            (
                pool: memoryPool,
                leaveOpen: true
            );

            TlsDuplexPipe tlsDuplexPipe = null;

            if (_options.RemoteCertificateMode == RemoteCertificateMode.NoCertificate)
            {
                tlsDuplexPipe = new TlsDuplexPipe(context.Transport, inputPipeOptions, outputPipeOptions);
            }
            else
            {
                tlsDuplexPipe = new TlsDuplexPipe(context.Transport, inputPipeOptions, outputPipeOptions, s => new SslStream(
                    s,
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        if (certificate == null)
                        {
                            return _options.RemoteCertificateMode != RemoteCertificateMode.RequireCertificate;
                        }

                        if (_options.RemoteCertificateValidation == null)
                        {
                            if (sslPolicyErrors != SslPolicyErrors.None)
                            {
                                return false;
                            }
                        }

                        var certificate2 = ConvertToX509Certificate2(certificate);
                        if (certificate2 == null)
                        {
                            return false;
                        }

                        if (_options.RemoteCertificateValidation != null)
                        {
                            if (!_options.RemoteCertificateValidation(certificate2, chain, sslPolicyErrors))
                            {
                                return false;
                            }
                        }

                        return true;
                    }));
            }

            var sslStream = tlsDuplexPipe.Stream;

            using (var cancellationTokeSource = new CancellationTokenSource(_options.HandshakeTimeout))
            using (cancellationTokeSource.Token.UnsafeRegister(state => ((ConnectionContext)state).Abort(), context))
            {
                try
                {
                    ClientCertificateSelectionCallback selector = null;
                    if (_certificateSelector != null)
                    {
                        selector = (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                        {
                            var cert = _certificateSelector(sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers);
                            if (cert != null)
                            {
                                EnsureCertificateIsAllowedForClientAuth(cert);
                            }

                            return cert;
                        };
                    }

                    var sslOptions = new TlsClientAuthenticationOptions
                    {
                        ClientCertificates = _certificate == null || _certificateSelector != null ? null : new X509CertificateCollection { _certificate },
                        LocalCertificateSelectionCallback = selector,
                        EnabledSslProtocols = _options.SslProtocols,
                    };

                    _options.OnAuthenticateAsClient?.Invoke(context, sslOptions);

                    await sslStream.AuthenticateAsClientAsync(sslOptions.Value, cancellationTokeSource.Token);
                }
                catch (OperationCanceledException ex)
                {
                    _logger?.LogWarning(2, ex, "Authentication timed out");
                    await sslStream.DisposeAsync();
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(1, ex, "Authentication failed");
                    await sslStream.DisposeAsync();
                    return;
                }
            }

            feature.ApplicationProtocol = sslStream.NegotiatedApplicationProtocol.Protocol;

            context.Features.Set<ITlsApplicationProtocolFeature>(feature);
            feature.LocalCertificate = ConvertToX509Certificate2(sslStream.LocalCertificate);
            feature.RemoteCertificate = ConvertToX509Certificate2(sslStream.RemoteCertificate);
            feature.CipherAlgorithm = sslStream.CipherAlgorithm;
            feature.CipherStrength = sslStream.CipherStrength;
            feature.HashAlgorithm = sslStream.HashAlgorithm;
            feature.HashStrength = sslStream.HashStrength;
            feature.KeyExchangeAlgorithm = sslStream.KeyExchangeAlgorithm;
            feature.KeyExchangeStrength = sslStream.KeyExchangeStrength;
            feature.Protocol = sslStream.SslProtocol;

            var originalTransport = context.Transport;

            try
            {
                context.Transport = tlsDuplexPipe;

                // Disposing the stream will dispose the tlsDuplexPipe
                await using (sslStream)
                await using (tlsDuplexPipe)
                {
                    await _next(context);
                    // Dispose the inner stream (tlsDuplexPipe) before disposing the SslStream
                    // as the duplex pipe can hit an ODE as it still may be writing.
                }
            }
            finally
            {
                // Restore the original so that it gets closed appropriately
                context.Transport = originalTransport;
            }
        }

        private static X509Certificate2 ValidateCertificate(X509Certificate2 certificate, RemoteCertificateMode mode)
        {
            switch (mode)
            {
                case RemoteCertificateMode.NoCertificate:
                    return null;
                case RemoteCertificateMode.AllowCertificate:
                    //if certificate exists but can not be used for client authentication.
                    if (certificate != null && CertificateLoader.IsCertificateAllowedForClientAuth(certificate))
                        return certificate;
                    return null;
                case RemoteCertificateMode.RequireCertificate:
                    EnsureCertificateIsAllowedForClientAuth(certificate);
                    return certificate;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        protected static void EnsureCertificateIsAllowedForClientAuth(X509Certificate2 certificate)
        {
            if (certificate is null)
            {
                throw new InvalidOperationException("No certificate provided for client authentication.");
            }

            if (!CertificateLoader.IsCertificateAllowedForClientAuth(certificate))
            {
                throw new InvalidOperationException($"Invalid client certificate for client authentication: {certificate.Thumbprint}");
            }
        }

        private static X509Certificate2 ConvertToX509Certificate2(X509Certificate certificate)
        {
            if (certificate is null)
            {
                return null;
            }

            return certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        }
    }
}
