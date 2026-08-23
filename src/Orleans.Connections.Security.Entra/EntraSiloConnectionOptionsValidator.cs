using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration;

internal sealed class EntraSiloConnectionOptionsValidator : IValidateOptions<EntraSiloConnectionOptions>
{
    private static readonly TimeSpan MaximumLongDuration = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaximumTokenDuration = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumRetrievalTimeout = TimeSpan.FromMinutes(5);
    public ValidateOptionsResult Validate(string? name, EntraSiloConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (options.Authority is not { IsAbsoluteUri: true } authority
            || !string.Equals(authority.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment))
        {
            errors.Add($"{nameof(options.Authority)} must be an absolute HTTPS URI without user information, a query, or a fragment.");
        }
        else
        {
            ValidateAuthority(authority, errors);
        }

        RequireValue(options.TokenScope, nameof(options.TokenScope), errors);
        RequireValue(options.ResourceApplicationId, nameof(options.ResourceApplicationId), errors);
        RequireNonEmpty(options.ValidTenantIds, nameof(options.ValidTenantIds), errors);
        RequireNonEmpty(options.AllowedAlgorithms, nameof(options.AllowedAlgorithms), errors);
        RequireNonEmpty(options.SupportedTokenVersions, nameof(options.SupportedTokenVersions), errors);
        ValidateEntries(options.ValidAudiences, nameof(options.ValidAudiences), errors);
        ValidateEntries(options.ValidTenantIds, nameof(options.ValidTenantIds), errors);
        ValidateEntries(options.AllowedClientIds, nameof(options.AllowedClientIds), errors);
        ValidateEntries(options.AllowedServicePrincipalObjectIds, nameof(options.AllowedServicePrincipalObjectIds), errors);
        ValidateEntries(options.RequiredRoles, nameof(options.RequiredRoles), errors);
        ValidateEntries(options.AllowedAlgorithms, nameof(options.AllowedAlgorithms), errors);
        ValidateEntries(options.SupportedTokenVersions, nameof(options.SupportedTokenVersions), errors);
        ValidateEntries(options.AdditionalTrustedMetadataHosts, nameof(options.AdditionalTrustedMetadataHosts), errors);

        if (!string.IsNullOrWhiteSpace(options.ResourceApplicationId)
            && !Guid.TryParse(options.ResourceApplicationId, out _))
        {
            errors.Add($"{nameof(options.ResourceApplicationId)} must be a GUID.");
        }

        if (options.Authority is { IsAbsoluteUri: true } configuredAuthority
            && TryGetAuthorityTenant(configuredAuthority, out var authorityTenant)
            && !options.ValidTenantIds.Contains(authorityTenant))
        {
            errors.Add($"{nameof(options.ValidTenantIds)} must include the tenant from {nameof(options.Authority)}.");
        }

        if (!options.AllowAnyApplicationInTenant
            && options.AllowedClientIds.Count == 0
            && options.AllowedServicePrincipalObjectIds.Count == 0
            && options.RequiredRoles.Count == 0)
        {
            errors.Add(
                $"At least one of {nameof(options.AllowedClientIds)}, {nameof(options.AllowedServicePrincipalObjectIds)}, " +
                $"or {nameof(options.RequiredRoles)} must be configured unless {nameof(options.AllowAnyApplicationInTenant)} is enabled.");
        }

        var hasClusterClaim = !string.IsNullOrWhiteSpace(options.ClusterClaimType);
        var hasExactClusterRole = !string.IsNullOrWhiteSpace(options.ClusterRole);
        var hasFormattedClusterRole = !string.IsNullOrWhiteSpace(options.ClusterRoleFormat);
        var hasClusterRole = hasExactClusterRole || hasFormattedClusterRole;
        if (hasExactClusterRole && hasFormattedClusterRole)
        {
            errors.Add(
                $"Only one of {nameof(options.ClusterRole)} or {nameof(options.ClusterRoleFormat)} can configure cluster role binding.");
        }

        if (!hasClusterClaim && !hasClusterRole)
        {
            errors.Add(
                $"A cluster role ({nameof(options.ClusterRole)} or {nameof(options.ClusterRoleFormat)}) " +
                $"or {nameof(options.ClusterClaimType)} must bind credentials to the local cluster.");
        }
        else if (hasClusterClaim && hasClusterRole)
        {
            errors.Add(
                $"Configure either a cluster role ({nameof(options.ClusterRole)} or {nameof(options.ClusterRoleFormat)}) " +
                $"or {nameof(options.ClusterClaimType)}, but not both.");
        }

        ValidateFormat(options.ClusterRoleFormat, nameof(options.ClusterRoleFormat), errors);
        ValidatePositive(options.MinimumRemainingTokenLifetime, nameof(options.MinimumRemainingTokenLifetime), MaximumTokenDuration, errors);
        ValidatePositive(options.MaximumTokenLifetime, nameof(options.MaximumTokenLifetime), MaximumTokenDuration, errors);
        ValidateNonNegative(options.ClockSkew, nameof(options.ClockSkew), errors);
        ValidatePositive(options.AutomaticMetadataRefreshInterval, nameof(options.AutomaticMetadataRefreshInterval), MaximumLongDuration, errors);
        ValidatePositive(options.UnknownSigningKeyRefreshInterval, nameof(options.UnknownSigningKeyRefreshInterval), MaximumLongDuration, errors);
        ValidatePositive(options.MetadataRefreshBackoff, nameof(options.MetadataRefreshBackoff), MaximumLongDuration, errors);
        ValidatePositive(options.MaximumMetadataRefreshBackoff, nameof(options.MaximumMetadataRefreshBackoff), MaximumLongDuration, errors);
        ValidatePositive(options.MetadataRetrievalTimeout, nameof(options.MetadataRetrievalTimeout), MaximumRetrievalTimeout, errors);
        ValidatePositive(options.LastKnownGoodLifetime, nameof(options.LastKnownGoodLifetime), MaximumLongDuration, errors);

        if (options.MinimumRemainingTokenLifetime > options.MaximumTokenLifetime)
        {
            errors.Add($"{nameof(options.MinimumRemainingTokenLifetime)} must not exceed {nameof(options.MaximumTokenLifetime)}.");
        }

        if (options.ClockSkew > MaximumClockSkew)
        {
            errors.Add($"{nameof(options.ClockSkew)} cannot exceed {MaximumClockSkew}.");
        }

        if (options.MaximumMetadataRefreshBackoff < options.MetadataRefreshBackoff)
        {
            errors.Add($"{nameof(options.MaximumMetadataRefreshBackoff)} must not be less than {nameof(options.MetadataRefreshBackoff)}.");
        }

        if (!double.IsFinite(options.MetadataRefreshJitterRatio)
            || options.MetadataRefreshJitterRatio is < 0 or > 1)
        {
            errors.Add($"{nameof(options.MetadataRefreshJitterRatio)} must be between 0 and 1.");
        }

        if (options.MaximumTokenSize <= 0)
        {
            errors.Add($"{nameof(options.MaximumTokenSize)} must be positive.");
        }
        else if (options.MaximumTokenSize > 1024 * 1024)
        {
            errors.Add($"{nameof(options.MaximumTokenSize)} cannot exceed 1 MiB.");
        }

        if (options.MaximumMetadataSize <= 0)
        {
            errors.Add($"{nameof(options.MaximumMetadataSize)} must be positive.");
        }
        else if (options.MaximumMetadataSize > 16 * 1024 * 1024)
        {
            errors.Add($"{nameof(options.MaximumMetadataSize)} cannot exceed 16 MiB.");
        }

        if (options.MaximumMetadataRefreshQueueSize is <= 0 or > 65_536)
        {
            errors.Add($"{nameof(options.MaximumMetadataRefreshQueueSize)} must be between 1 and 65536.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateAuthority(Uri authority, List<string> errors)
    {
        if (!TryGetAuthorityTenant(authority, out var tenant))
        {
            errors.Add($"{nameof(EntraSiloConnectionOptions.Authority)} must contain a tenant-specific path.");
            return;
        }

        if (tenant is "common" or "organizations" or "consumers")
        {
            errors.Add($"{nameof(EntraSiloConnectionOptions.Authority)} must identify a specific tenant.");
        }
    }

    private static bool TryGetAuthorityTenant(Uri authority, out string tenant)
    {
        var segments = authority.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            tenant = string.Empty;
            return false;
        }

        tenant = string.Equals(segments[^1], "v2.0", StringComparison.OrdinalIgnoreCase) && segments.Length > 1
            ? segments[^2]
            : segments[^1];
        return !string.IsNullOrWhiteSpace(tenant);
    }

    private static void RequireValue(string? value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{propertyName} must be configured.");
        }
    }

    private static void RequireNonEmpty(ISet<string> values, string propertyName, List<string> errors)
    {
        if (values.Count == 0)
        {
            errors.Add($"{propertyName} must contain at least one value.");
        }
    }

    private static void ValidateEntries(ISet<string> values, string propertyName, List<string> errors)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{propertyName} cannot contain null, empty, or whitespace values.");
                break;
            }
        }
    }

    private static void ValidateFormat(string? value, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            var marker = Guid.NewGuid().ToString("N");
            if (!string.Format(CultureInfo.InvariantCulture, value, marker).Contains(marker, StringComparison.Ordinal))
            {
                errors.Add($"{propertyName} must contain the '{{0}}' cluster identifier placeholder.");
            }
        }
        catch (FormatException)
        {
            errors.Add($"{propertyName} must be a valid composite format string.");
        }
    }

    private static void ValidatePositive(
        TimeSpan value,
        string propertyName,
        TimeSpan maximum,
        List<string> errors)
    {
        if (value <= TimeSpan.Zero || value > maximum)
        {
            errors.Add($"{propertyName} must be positive and no greater than {maximum}.");
        }
    }

    private static void ValidateNonNegative(TimeSpan value, string propertyName, List<string> errors)
    {
        if (value < TimeSpan.Zero)
        {
            errors.Add($"{propertyName} cannot be negative.");
        }
    }
}
