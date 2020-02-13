using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Security.Internal;

namespace Orleans.Connections.Security
{
    internal class TlsClientConnectionMiddleware
    {
        private readonly ConnectionDelegate _next;
        private readonly TlsOptions _options;
        private readonly ILogger _logger;
        private readonly X509Certificate2 _certificate;

        public TlsClientConnectionMiddleware(ConnectionDelegate next, TlsOptions options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _next = next;

            // capture the certificate now so it can't be switched after validation
            _certificate = ValidateCertificate(options.LocalCertificate, options.ClientCertificateMode);


            _options = options;
            _logger = loggerFactory?.CreateLogger<TlsServerConnectionMiddleware>();
        }

        public Task OnConnectionAsync(ConnectionContext context)
        {
            return Task.Run(() => InnerOnConnectionAsync(context));
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
            using (cancellationTokeSource.Token.UnsafeRegisterCancellation(state => ((ConnectionContext)state).Abort(), context))
            {
                try
                {
                    var sslOptions = new TlsClientAuthenticationOptions
                    {
                        ClientCertificates = _certificate == null ? null : new X509CertificateCollection(new[] { _certificate }),
                        EnabledSslProtocols = _options.SslProtocols,
                    };

                    _options.OnAuthenticateAsClient?.Invoke(context, sslOptions);

#if NETCOREAPP
                    await sslStream.AuthenticateAsClientAsync(sslOptions.Value, cancellationTokeSource.Token);
#else
                    await sslStream.AuthenticateAsClientAsync(
                        sslOptions.TargetHost,
                        sslOptions.ClientCertificates,
                        sslOptions.EnabledSslProtocols,
                        sslOptions.CertificateRevocationCheckMode == X509RevocationMode.Online);
#endif
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogDebug(2, "Authentication timed out");
#if NETCOREAPP
                    await sslStream.DisposeAsync();
#else
                    sslStream.Dispose();
#endif
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is AuthenticationException)
                {
                    _logger?.LogDebug(1, ex, "Authentication failed");
#if NETCOREAPP
                    await sslStream.DisposeAsync();
#else
                    sslStream.Dispose();
#endif
                    return;
                }
            }

#if NETCOREAPP
            feature.ApplicationProtocol = sslStream.NegotiatedApplicationProtocol.Protocol;
#endif

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
#if NETCOREAPP
                await using (sslStream)
                await using (tlsDuplexPipe)
#else
                using (sslStream)
                using (tlsDuplexPipe)
#endif
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
            if (certificate == null)
                throw new InvalidOperationException("No certificate provided for client authentication.");

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
