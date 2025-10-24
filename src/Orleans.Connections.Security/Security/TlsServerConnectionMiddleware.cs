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
    internal class TlsServerConnectionMiddleware
    {
        private readonly ConnectionDelegate _next;
        private readonly TlsOptions _options;
        private readonly ILogger _logger;
        private readonly X509Certificate2 _certificate;
        private readonly Func<ConnectionContext, string, X509Certificate2> _certificateSelector;

        public TlsServerConnectionMiddleware(ConnectionDelegate next, TlsOptions options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _next = next;

            // capture the certificate now so it can't be switched after validation
            _certificate = options.LocalCertificate;
            _certificateSelector = options.LocalServerCertificateSelector;
            if (_certificate == null && _certificateSelector == null)
            {
                throw new ArgumentException("Server certificate is required", nameof(options));
            }

            // If a selector is provided then ignore the cert, it may be a default cert.
            if (_certificateSelector != null)
            {
                // SslStream doesn't allow both.
                _certificate = null;
            }
            else
            {
                EnsureCertificateIsAllowedForServerAuth(_certificate);
            }

            _options = options;
            _logger = loggerFactory?.CreateLogger<TlsServerConnectionMiddleware>();
        }

        public Task OnConnectionAsync(ConnectionContext context)
        {
            return InnerOnConnectionAsync(context);
        }

        private async Task InnerOnConnectionAsync(ConnectionContext context)
        {
            bool certificateRequired;
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
                certificateRequired = false;
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

                certificateRequired = true;
            }

            var sslStream = tlsDuplexPipe.Stream;

            using (var cancellationTokeSource = new CancellationTokenSource(_options.HandshakeTimeout))
            using (cancellationTokeSource.Token.UnsafeRegister(state => ((ConnectionContext)state).Abort(), context))
            {
                try
                {
                    // Adapt to the SslStream signature
                    ServerCertificateSelectionCallback selector = null;
                    if (_certificateSelector != null)
                    {
                        selector = (sender, name) =>
                        {
                            context.Features.Set(sslStream);
                            var cert = _certificateSelector(context, name);
                            if (cert != null)
                            {
                                EnsureCertificateIsAllowedForServerAuth(cert);
                            }

                            return cert;
                        };
                    }

                    var sslOptions = new TlsServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ServerCertificateSelectionCallback = selector,
                        ClientCertificateRequired = certificateRequired,
                        EnabledSslProtocols = _options.SslProtocols,
                        CertificateRevocationCheckMode = _options.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                    };

                    _options.OnAuthenticateAsServer?.Invoke(context, sslOptions);

                    await sslStream.AuthenticateAsServerAsync(sslOptions.Value, cancellationTokeSource.Token);
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

                // Disposing the stream will dispose the TlsDuplexPipe
                await using (sslStream)
                await using (tlsDuplexPipe)
                {
                    await _next(context);
                    // Dispose the inner stream (TlsDuplexPipe) before disposing the SslStream
                    // as the duplex pipe can hit an ODE as it still may be writing.
                }
            }
            finally
            {
                // Restore the original so that it gets closed appropriately
                context.Transport = originalTransport;
            }
        }

        protected static void EnsureCertificateIsAllowedForServerAuth(X509Certificate2 certificate)
        {
            if (!CertificateLoader.IsCertificateAllowedForServerAuth(certificate))
            {
                throw new InvalidOperationException($"Invalid server certificate for server authentication: {certificate.Thumbprint}");
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
