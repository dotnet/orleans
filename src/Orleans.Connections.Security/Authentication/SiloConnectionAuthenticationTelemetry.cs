using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Orleans.Connections.Security;

internal enum AuthenticationResultCategory
{
    Authenticated,
    AcceptedUnauthenticated,
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
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode,
        string protocol,
        AuthenticationResultCategory result)
    {
        var tags = CreateTags(direction, mode, protocol, result);
        Attempts.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
    }

    public static void RecordFallback(
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode)
    {
        var tags = CreateTags(direction, mode, "Orleans1", AuthenticationResultCategory.BaselineFallback);
        ProtocolFallbacks.Add(1, tags);
    }

    public static void RecordEvent(
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode,
        string protocol,
        AuthenticationResultCategory result)
    {
        Attempts.Add(1, CreateTags(direction, mode, protocol, result));
    }

    public static void AddActive(
        long value,
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode,
        string protocol)
    {
        Active.Add(value, CreateTags(direction, mode, protocol, AuthenticationResultCategory.Authenticated));
    }

    private static TagList CreateTags(
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationMode mode,
        string protocol,
        AuthenticationResultCategory result)
    {
        return new TagList
        {
            { "direction", direction == SiloConnectionAuthenticationDirection.Inbound ? "inbound" : "outbound" },
            { "mode", mode.ToString() },
            { "protocol.version", protocol },
            { "result", GetResultName(result) },
        };
    }

    public static string GetResultName(AuthenticationResultCategory result) => result switch
    {
        AuthenticationResultCategory.Authenticated => "authenticated",
        AuthenticationResultCategory.AcceptedUnauthenticated => "accepted_unauthenticated",
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
        Message = "Silo connection authentication failed. Direction: {Direction}; Mode: {Mode}; Category: {Category}.")]
    public static partial void LogFailure(ILogger logger, string direction, string mode, string category);

    [LoggerMessage(
        EventId = 9201,
        Level = LogLevel.Information,
        Message = "Silo connection authentication completed. Direction: {Direction}; Mode: {Mode}; Result: {Result}.")]
    public static partial void LogCompleted(ILogger logger, string direction, string mode, string result);

    [LoggerMessage(
        EventId = 9202,
        Level = LogLevel.Information,
        Message = "Silo connection authentication used the baseline protocol in Audit mode. Direction: {Direction}.")]
    public static partial void LogFallback(ILogger logger, string direction);
}
