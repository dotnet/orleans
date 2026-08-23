using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Orleans.Connections.Security;

internal enum AuthenticationResultCategory
{
    Authenticated,
    BaselineFallback,
    Rejected,
    Overload,
    Timeout,
    ProtocolError,
    TlsPolicyError,
    AcquisitionFailure,
    ValidationFailure,
    AuthorizationFailure,
    Expiration,
}

internal static partial class SiloConnectionAuthenticationTelemetry
{
    private static readonly Meter Meter = new("Microsoft.Orleans.Connections.Security");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("orleans.connections.authentication.attempts");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("orleans.connections.authentication.duration", "ms");
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("orleans.connections.authentication.active");
    private static readonly Counter<long> ProtocolFallbacks = Meter.CreateCounter<long>("orleans.connections.authentication.protocol_fallbacks");

    public static long Start() => Stopwatch.GetTimestamp();

    public static void RecordAttempt(
        long started,
        SiloConnectionAuthenticationTarget target,
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode,
        string protocol,
        AuthenticationResultCategory result)
    {
        var tags = CreateTags(target, direction, mode, protocol, result);
        Attempts.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
    }

    public static void RecordFallback(
        SiloConnectionAuthenticationTarget target,
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode)
    {
        var tags = CreateTags(target, direction, mode, "Orleans1", AuthenticationResultCategory.BaselineFallback);
        ProtocolFallbacks.Add(1, tags);
    }

    public static void RecordEvent(
        SiloConnectionAuthenticationTarget target,
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode,
        string protocol,
        AuthenticationResultCategory result)
    {
        Attempts.Add(1, CreateTags(target, direction, mode, protocol, result));
    }

    public static void AddActive(
        long value,
        SiloConnectionAuthenticationTarget target,
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode,
        string protocol)
    {
        Active.Add(value, CreateTags(target, direction, mode, protocol, AuthenticationResultCategory.Authenticated));
    }

    private static TagList CreateTags(
        SiloConnectionAuthenticationTarget target,
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode,
        string protocol,
        AuthenticationResultCategory result)
    {
        return new TagList
        {
            { "connection.type", target == SiloConnectionAuthenticationTarget.Silo ? "silo" : "client" },
            { "direction", direction == SiloConnectionAuthenticationDirection.Inbound ? "inbound" : "outbound" },
            { "mode", GetModeName(mode) },
            { "protocol.version", protocol },
            { "result", GetResultName(result) },
        };
    }

    public static string GetModeName(SiloConnectionAuthenticationMode mode) => mode switch
    {
        SiloConnectionAuthenticationMode.Disabled => "Disabled",
        SiloConnectionAuthenticationMode.Audit => "Audit",
        SiloConnectionAuthenticationMode.Required => "Required",
        _ => "Unknown",
    };

    public static string GetResultName(AuthenticationResultCategory result) => result switch
    {
        AuthenticationResultCategory.Authenticated => "authenticated",
        AuthenticationResultCategory.BaselineFallback => "baseline_fallback",
        AuthenticationResultCategory.Rejected => "rejected",
        AuthenticationResultCategory.Overload => "overload",
        AuthenticationResultCategory.Timeout => "timeout",
        AuthenticationResultCategory.ProtocolError => "protocol_error",
        AuthenticationResultCategory.TlsPolicyError => "tls_policy_error",
        AuthenticationResultCategory.AcquisitionFailure => "acquisition_failure",
        AuthenticationResultCategory.ValidationFailure => "validation_failure",
        AuthenticationResultCategory.AuthorizationFailure => "authorization_failure",
        AuthenticationResultCategory.Expiration => "expiration",
        _ => "protocol_error",
    };

    [LoggerMessage(
        EventId = 9200,
        Level = LogLevel.Warning,
        Message = "Orleans connection authentication failed. Connection type: {ConnectionType}; Direction: {Direction}; Mode: {Mode}; Category: {Category}.")]
    public static partial void LogFailure(
        ILogger logger,
        string connectionType,
        string direction,
        string mode,
        string category);

    [LoggerMessage(
        EventId = 9201,
        Level = LogLevel.Information,
        Message = "Orleans connection authentication completed. Connection type: {ConnectionType}; Direction: {Direction}; Mode: {Mode}; Result: {Result}.")]
    public static partial void LogCompleted(
        ILogger logger,
        string connectionType,
        string direction,
        string mode,
        string result);

    [LoggerMessage(
        EventId = 9202,
        Level = LogLevel.Information,
        Message = "Orleans connection authentication used the baseline protocol in Audit mode. Connection type: {ConnectionType}; Direction: {Direction}.")]
    public static partial void LogFallback(ILogger logger, string connectionType, string direction);
}
