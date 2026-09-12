using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal sealed class StrictHttpDocumentRetriever : IDocumentRetriever, IDisposable
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly HttpClient _httpClient;
    private readonly HashSet<string> _trustedHosts;
    private readonly EntraSiloConnectionOptions _options;

    public StrictHttpDocumentRetriever(EntraSiloConnectionOptions options)
        : this(options, new SocketsHttpHandler { AllowAutoRedirect = false })
    {
    }

    internal StrictHttpDocumentRetriever(EntraSiloConnectionOptions options, HttpMessageHandler handler)
    {
        _options = options;
        _trustedHosts = new HashSet<string>(options.AdditionalTrustedMetadataHosts, StringComparer.OrdinalIgnoreCase)
        {
            options.Authority!.IdnHost,
        };

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !_trustedHosts.Contains(uri.IdnHost)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.ProviderUnavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(_options.MetadataRetrievalTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);

        if (IsRedirect(response.StatusCode) || response.StatusCode != HttpStatusCode.OK)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.ProviderUnavailable);
        }

        if (response.Content.Headers.ContentLength is > 0 and var contentLength
            && contentLength > _options.MaximumMetadataSize)
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.ProviderUnavailable);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(_options.MaximumMetadataSize, 16 * 1024));
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > _options.MaximumMetadataSize)
                {
                    throw new EntraAuthenticationException(EntraAuthenticationError.ProviderUnavailable);
                }

                buffer.Write(rented, 0, read);
            }

            try
            {
                return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            }
            catch (DecoderFallbackException)
            {
                throw new EntraAuthenticationException(EntraAuthenticationError.ProviderUnavailable);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool IsRedirect(HttpStatusCode statusCode)
        => (int)statusCode is >= 300 and <= 399;
}
