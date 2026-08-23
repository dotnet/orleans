using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Security;

internal sealed class SiloConnectionAuthenticationOptionsValidator : IValidateOptions<SiloConnectionAuthenticationOptions>
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(1);
    private readonly ConnectionAuthenticationRegistration _registration;

    public SiloConnectionAuthenticationOptionsValidator(ConnectionAuthenticationRegistration registration)
    {
        _registration = registration;
    }

    public ValidateOptionsResult Validate(string? name, SiloConnectionAuthenticationOptions options)
    {
        if (!string.Equals(name, _registration.Name, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();

        if (!Enum.IsDefined(options.Mode))
        {
            failures.Add($"{nameof(options.Mode)} is invalid.");
        }

        ValidatePositiveDuration(options.TokenExchangeTimeout, nameof(options.TokenExchangeTimeout), failures);
        ValidatePositiveDuration(options.MinimumRemainingTokenLifetime, nameof(options.MinimumRemainingTokenLifetime), failures);
        ValidateNonNegativeDuration(options.ExpirationSafetyMargin, nameof(options.ExpirationSafetyMargin), failures);
        ValidateNonNegativeDuration(options.ExpirationJitter, nameof(options.ExpirationJitter), failures);
        ValidatePositiveBounded(options.MaxTokenSize, nameof(options.MaxTokenSize), 1024 * 1024, failures);
        ValidatePositiveBounded(options.MaxConcurrentInboundAuthentications, nameof(options.MaxConcurrentInboundAuthentications), 65_536, failures);
        ValidatePositiveBounded(options.MaxConcurrentOutboundAuthentications, nameof(options.MaxConcurrentOutboundAuthentications), 65_536, failures);
        ValidateNonNegativeBounded(options.MaxPendingInboundAuthentications, nameof(options.MaxPendingInboundAuthentications), 65_536, failures);
        ValidateNonNegativeBounded(options.MaxPendingOutboundAuthentications, nameof(options.MaxPendingOutboundAuthentications), 65_536, failures);

        if (options.TimeProvider is null)
        {
            failures.Add($"{nameof(options.TimeProvider)} is required.");
        }

        if (options.Mode != SiloConnectionAuthenticationMode.Disabled)
        {
            if (_registration.RequiresTokenProvider && !_registration.HasTokenProvider)
            {
                failures.Add($"{options.Mode} mode needs exactly one token provider.");
            }

            if (_registration.RequiresTokenValidator && !_registration.HasTokenValidator)
            {
                failures.Add($"{options.Mode} mode needs exactly one token validator.");
            }
        }

        if (options.Mode == SiloConnectionAuthenticationMode.Required)
        {
            if (_registration.TlsOptions.RemoteCertificateValidation is not null)
            {
                failures.Add("Required mode does not permit custom remote-certificate validation callbacks.");
            }

            if (_registration.RequiresTokenProvider && string.IsNullOrWhiteSpace(options.TargetHost))
            {
                failures.Add($"Required mode needs a non-empty {nameof(options.TargetHost)} for TLS endpoint-identity validation.");
            }

            var allowedProtocols = System.Security.Authentication.SslProtocols.None
                | System.Security.Authentication.SslProtocols.Tls12
                | System.Security.Authentication.SslProtocols.Tls13;
            if ((_registration.TlsOptions.SslProtocols & ~allowedProtocols) != 0)
            {
                failures.Add("Required mode permits only TLS 1.2 or later.");
            }
        }

        if (options.MinimumRemainingTokenLifetime < options.ExpirationSafetyMargin + options.ExpirationJitter)
        {
            failures.Add($"{nameof(options.MinimumRemainingTokenLifetime)} must cover the expiration safety margin and jitter.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePositiveDuration(TimeSpan value, string name, List<string> failures)
    {
        if (value <= TimeSpan.Zero || value > MaxDuration)
        {
            failures.Add($"{name} must be positive and no greater than one day.");
        }
    }

    private static void ValidateNonNegativeDuration(TimeSpan value, string name, List<string> failures)
    {
        if (value < TimeSpan.Zero || value > MaxDuration)
        {
            failures.Add($"{name} must be non-negative and no greater than one day.");
        }
    }

    private static void ValidatePositiveBounded(int value, string name, int maximum, List<string> failures)
    {
        if (value <= 0 || value > maximum)
        {
            failures.Add($"{name} must be between 1 and {maximum}.");
        }
    }

    private static void ValidateNonNegativeBounded(int value, string name, int maximum, List<string> failures)
    {
        if (value < 0 || value > maximum)
        {
            failures.Add($"{name} must be between 0 and {maximum}.");
        }
    }
}
