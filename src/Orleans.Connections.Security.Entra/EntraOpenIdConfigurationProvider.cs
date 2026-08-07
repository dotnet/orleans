using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra;

internal readonly record struct EntraOpenIdConfigurationSnapshot(
    OpenIdConnectConfiguration Configuration,
    long Generation);

internal sealed class EntraOpenIdConfigurationProvider : IDisposable
{
    private readonly EntraSiloConnectionOptions _options;
    private readonly IDocumentRetriever _documentRetriever;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _nextJitter;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _stateLock = new();
    private ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private OpenIdConnectConfiguration? _lastKnownGood;
    private DateTimeOffset _lastKnownGoodAt;
    private DateTimeOffset _nextAutomaticRefresh;
    private DateTimeOffset _nextRefreshAllowed;
    private DateTimeOffset _lastUnknownKeyRefresh;
    private long _generation;
    private int _consecutiveFailures;
    private int _queuedRefreshes;

    public EntraOpenIdConfigurationProvider(EntraSiloConnectionOptions options, TimeProvider timeProvider)
        : this(options, new StrictHttpDocumentRetriever(options), timeProvider, Random.Shared.NextDouble)
    {
    }

    internal EntraOpenIdConfigurationProvider(
        EntraSiloConnectionOptions options,
        IDocumentRetriever documentRetriever,
        TimeProvider timeProvider,
        Func<double> nextJitter)
    {
        _options = options;
        _documentRetriever = documentRetriever;
        _timeProvider = timeProvider;
        _nextJitter = nextJitter;
        _configurationManager = CreateConfigurationManager();
    }

    public ValueTask<EntraOpenIdConfigurationSnapshot> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_stateLock)
        {
            if (_lastKnownGood is not null
                && now < _nextAutomaticRefresh
                && now - _lastKnownGoodAt <= _options.LastKnownGoodLifetime)
            {
                return ValueTask.FromResult(new EntraOpenIdConfigurationSnapshot(_lastKnownGood, _generation));
            }
        }

        return RefreshAsync(RefreshReason.Automatic, observedGeneration: -1, cancellationToken);
    }

    public ValueTask<EntraOpenIdConfigurationSnapshot> RefreshForUnknownSigningKeyAsync(
        long observedGeneration,
        CancellationToken cancellationToken)
        => RefreshAsync(RefreshReason.UnknownSigningKey, observedGeneration, cancellationToken);

    public void Dispose()
    {
        _refreshLock.Dispose();
        if (_documentRetriever is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async ValueTask<EntraOpenIdConfigurationSnapshot> RefreshAsync(
        RefreshReason reason,
        long observedGeneration,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _queuedRefreshes) > _options.MaximumMetadataRefreshQueueSize)
        {
            Interlocked.Decrement(ref _queuedRefreshes);
            throw new EntraAuthenticationException(EntraAuthenticationError.ProviderUnavailable);
        }

        try
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _queuedRefreshes);
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            lock (_stateLock)
            {
                if (_lastKnownGood is not null)
                {
                    if (reason == RefreshReason.Automatic && now < _nextAutomaticRefresh)
                    {
                        return new EntraOpenIdConfigurationSnapshot(_lastKnownGood, _generation);
                    }

                    if (reason == RefreshReason.UnknownSigningKey
                        && (observedGeneration != _generation
                            || now - _lastUnknownKeyRefresh < _options.UnknownSigningKeyRefreshInterval))
                    {
                        return new EntraOpenIdConfigurationSnapshot(_lastKnownGood, _generation);
                    }
                }

                if (now < _nextRefreshAllowed)
                {
                    return GetLastKnownGoodOrThrow(now);
                }

                if (reason == RefreshReason.UnknownSigningKey)
                {
                    _lastUnknownKeyRefresh = now;
                }

                // A fresh manager guarantees that an allowed refresh is not delayed by a second,
                // wall-clock-based throttle inside ConfigurationManager.
                _configurationManager = CreateConfigurationManager();
            }

            try
            {
                var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                ValidateConfiguration(configuration);

                lock (_stateLock)
                {
                    now = _timeProvider.GetUtcNow();
                    _lastKnownGood = configuration;
                    _lastKnownGoodAt = now;
                    _nextAutomaticRefresh = now + Min(
                        _options.AutomaticMetadataRefreshInterval,
                        _options.LastKnownGoodLifetime);
                    _nextRefreshAllowed = DateTimeOffset.MinValue;
                    _consecutiveFailures = 0;
                    _generation++;
                    return new EntraOpenIdConfigurationSnapshot(configuration, _generation);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                EntraAuthenticationException
                or IOException
                or HttpRequestException
                or InvalidOperationException
                or OperationCanceledException
                or ArgumentException
                or TimeoutException
                or SecurityTokenException
                or JsonException)
            {
                lock (_stateLock)
                {
                    now = _timeProvider.GetUtcNow();
                    _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 30);
                    _nextRefreshAllowed = now + GetBackoffDelay(_consecutiveFailures);
                    return GetLastKnownGoodOrThrow(now);
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private EntraOpenIdConfigurationSnapshot GetLastKnownGoodOrThrow(DateTimeOffset now)
    {
        if (_lastKnownGood is not null && now - _lastKnownGoodAt <= _options.LastKnownGoodLifetime)
        {
            return new EntraOpenIdConfigurationSnapshot(_lastKnownGood, _generation);
        }

        throw new EntraAuthenticationException(EntraAuthenticationError.ProviderUnavailable);
    }

    private TimeSpan GetBackoffDelay(int failureCount)
    {
        var exponent = Math.Min(failureCount - 1, 30);
        var baseMilliseconds = Math.Min(
            _options.MetadataRefreshBackoff.TotalMilliseconds * Math.Pow(2, exponent),
            _options.MaximumMetadataRefreshBackoff.TotalMilliseconds);
        var jitter = baseMilliseconds * _options.MetadataRefreshJitterRatio * Math.Clamp(_nextJitter(), 0, 1);
        return TimeSpan.FromMilliseconds(Math.Min(
            baseMilliseconds + jitter,
            _options.MaximumMetadataRefreshBackoff.TotalMilliseconds));
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private ConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager()
    {
        var authority = _options.Authority!.AbsoluteUri.TrimEnd('/');
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            _documentRetriever);
    }

    private void ValidateConfiguration(OpenIdConnectConfiguration configuration)
    {
        if (!TryGetTrustedUri(configuration.Issuer, out var issuer)
            || !TryGetTrustedUri(configuration.JwksUri, out _)
            || !IssuerMatchesTenant(issuer)
            || configuration.JsonWebKeySet?.Keys.Any(key => EntraSigningKey.IsUsable(key, _options)) != true
            || !configuration.SigningKeys.Any(key => EntraSigningKey.IsUsable(key, configuration, _options)))
        {
            throw new EntraAuthenticationException(EntraAuthenticationError.ProviderUnavailable);
        }
    }

    private bool TryGetTrustedUri(string? value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return string.Equals(uri.IdnHost, _options.Authority!.IdnHost, StringComparison.OrdinalIgnoreCase)
            || _options.AdditionalTrustedMetadataHosts.Contains(uri.IdnHost);
    }

    private bool IssuerMatchesTenant(Uri issuer)
    {
        var segments = issuer.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => _options.ValidTenantIds.Contains(segment));
    }

    private enum RefreshReason
    {
        Automatic,
        UnknownSigningKey,
    }
}
