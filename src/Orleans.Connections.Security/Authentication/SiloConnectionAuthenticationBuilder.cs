using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Connections.Security;

/// <summary>
/// Configures providers and policy for authenticated silo connections.
/// </summary>
public sealed class SiloConnectionAuthenticationBuilder
{
    private readonly SiloConnectionAuthenticationOptions _options;
    private readonly IServiceCollection _services;

    internal SiloConnectionAuthenticationBuilder(
        SiloConnectionAuthenticationOptions options,
        IServiceCollection services)
    {
        _options = options;
        _services = services;
    }

    internal bool HasTokenProvider { get; private set; }

    internal bool HasTokenValidator { get; private set; }

    /// <summary>Gets the service collection used to configure authentication providers.</summary>
    public IServiceCollection Services => _services;

    /// <summary>Gets or sets the authentication enforcement mode.</summary>
    public SiloConnectionAuthenticationMode Mode { get => _options.Mode; set => _options.Mode = value; }

    /// <summary>Gets or sets the total token exchange timeout.</summary>
    public TimeSpan TokenExchangeTimeout { get => _options.TokenExchangeTimeout; set => _options.TokenExchangeTimeout = value; }

    /// <summary>Gets or sets the maximum UTF-8 token size in bytes.</summary>
    public int MaxTokenSize { get => _options.MaxTokenSize; set => _options.MaxTokenSize = value; }

    /// <summary>Gets or sets the maximum concurrent inbound authentication operations.</summary>
    public int MaxConcurrentInboundAuthentications
    {
        get => _options.MaxConcurrentInboundAuthentications;
        set => _options.MaxConcurrentInboundAuthentications = value;
    }

    /// <summary>Gets or sets the maximum concurrent outbound authentication operations.</summary>
    public int MaxConcurrentOutboundAuthentications
    {
        get => _options.MaxConcurrentOutboundAuthentications;
        set => _options.MaxConcurrentOutboundAuthentications = value;
    }

    /// <summary>Gets or sets the maximum queued inbound authentication operations.</summary>
    public int MaxPendingInboundAuthentications
    {
        get => _options.MaxPendingInboundAuthentications;
        set => _options.MaxPendingInboundAuthentications = value;
    }

    /// <summary>Gets or sets the maximum queued outbound authentication operations.</summary>
    public int MaxPendingOutboundAuthentications
    {
        get => _options.MaxPendingOutboundAuthentications;
        set => _options.MaxPendingOutboundAuthentications = value;
    }

    /// <summary>Gets or sets the minimum acceptable remaining credential lifetime.</summary>
    public TimeSpan MinimumRemainingTokenLifetime
    {
        get => _options.MinimumRemainingTokenLifetime;
        set => _options.MinimumRemainingTokenLifetime = value;
    }

    /// <summary>Gets or sets how long before credential expiration an authenticated connection is closed.</summary>
    public TimeSpan ExpirationSafetyMargin
    {
        get => _options.ExpirationSafetyMargin;
        set => _options.ExpirationSafetyMargin = value;
    }

    /// <summary>Gets or sets the maximum deterministic per-connection expiration jitter.</summary>
    public TimeSpan ExpirationJitter
    {
        get => _options.ExpirationJitter;
        set => _options.ExpirationJitter = value;
    }

    /// <summary>Gets or sets whether credentials without a finite expiration are accepted.</summary>
    public bool AllowNonExpiringCredentials
    {
        get => _options.AllowNonExpiringCredentials;
        set => _options.AllowNonExpiringCredentials = value;
    }

    /// <summary>Gets or sets the expected TLS server DNS identity and SNI name.</summary>
    public string? TargetHost { get => _options.TargetHost; set => _options.TargetHost = value; }

    /// <summary>Gets or sets the time provider used for timeouts and expiration.</summary>
    public TimeProvider TimeProvider { get => _options.TimeProvider; set => _options.TimeProvider = value; }

    /// <summary>Registers a singleton token provider.</summary>
    public SiloConnectionAuthenticationBuilder UseTokenProvider<TProvider>()
        where TProvider : class, ISiloConnectionTokenProvider
    {
        EnsureProviderCanBeRegistered();
        _services.AddSingleton<ISiloConnectionTokenProvider, TProvider>();
        HasTokenProvider = true;
        return this;
    }

    /// <summary>Registers a token provider instance.</summary>
    public SiloConnectionAuthenticationBuilder UseTokenProvider(ISiloConnectionTokenProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        EnsureProviderCanBeRegistered();
        _services.AddSingleton(provider);
        HasTokenProvider = true;
        return this;
    }

    /// <summary>Registers a singleton token validator.</summary>
    public SiloConnectionAuthenticationBuilder UseTokenValidator<TValidator>()
        where TValidator : class, ISiloConnectionTokenValidator
    {
        EnsureValidatorCanBeRegistered();
        _services.AddSingleton<ISiloConnectionTokenValidator, TValidator>();
        HasTokenValidator = true;
        return this;
    }

    /// <summary>Registers a token validator instance.</summary>
    public SiloConnectionAuthenticationBuilder UseTokenValidator(ISiloConnectionTokenValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        EnsureValidatorCanBeRegistered();
        _services.AddSingleton(validator);
        HasTokenValidator = true;
        return this;
    }

    private void EnsureProviderCanBeRegistered()
    {
        if (HasTokenProvider || _services.Any(descriptor => descriptor.ServiceType == typeof(ISiloConnectionTokenProvider)))
        {
            throw new InvalidOperationException("A silo connection token provider is already registered.");
        }
    }

    private void EnsureValidatorCanBeRegistered()
    {
        if (HasTokenValidator || _services.Any(descriptor => descriptor.ServiceType == typeof(ISiloConnectionTokenValidator)))
        {
            throw new InvalidOperationException("A silo connection token validator is already registered.");
        }
    }
}
