using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Messaging;
using Xunit;

namespace Orleans.Connections.Security.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
public class SiloConnectionAuthenticationMiddlewareTests
{
    private const byte TokenFrameType = 0x01;
    private const byte ResultFrameType = 0x02;
    private const byte AuthenticatedResult = 0x01;
    private const byte AcceptedUnauthenticatedResult = 0x02;
    private const byte RejectedResult = 0x03;
    private static readonly DateTimeOffset Now = new(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiration = Now.AddMinutes(30);

    [Theory]
    [InlineData(SiloConnectionAuthenticationDirection.Inbound)]
    [InlineData(SiloConnectionAuthenticationDirection.Outbound)]
    public async Task NegotiatedAuthenticationFailure_AbortsInRequiredAndAuditAndReportsSameCategory(
        SiloConnectionAuthenticationDirection direction)
    {
        var results = new List<ScenarioResult>();

        foreach (var mode in new[] { SiloConnectionAuthenticationMode.Required, SiloConnectionAuthenticationMode.Audit })
        {
            results.Add(await RunNegotiatedFailureAsync(mode, direction));
        }

        var expectedCategory = direction == SiloConnectionAuthenticationDirection.Inbound
            ? "authorization_failure"
            : "rejected";
        Assert.All(results, result =>
        {
            Assert.Equal(0, result.DownstreamCalls);
            Assert.NotNull(result.AbortReason);
            Assert.Equal(
                $"Orleans connection authentication failed ({expectedCategory}).",
                result.AbortReason.Message);
            Assert.Null(result.Feature);
            Assert.Equal(expectedCategory, Assert.Single(result.FailureLogs).Properties["Category"]);
            Assert.Equal(expectedCategory, Assert.Single(result.AttemptMetrics).Tags["result"]);
            Assert.DoesNotContain(result.Logs, entry => entry.EventId == 9201);
            Assert.DoesNotContain(
                result.AttemptMetrics,
                measurement => measurement.Tags["result"] is "authenticated" or "accepted_unauthenticated");
        });
        Assert.Equal(
            results[0].FailureLogs.Single().Properties["Category"],
            results[1].FailureLogs.Single().Properties["Category"]);

        if (direction == SiloConnectionAuthenticationDirection.Inbound)
        {
            Assert.All(results, result =>
            {
                Assert.Equal(ResultFrameType, result.PeerFrameType);
                Assert.Equal([RejectedResult], result.PeerPayload);
            });
        }
        else
        {
            Assert.All(results, result =>
            {
                Assert.Equal(TokenFrameType, result.PeerFrameType);
                Assert.Equal("fixed-outbound-token", Encoding.UTF8.GetString(result.PeerPayload));
            });
        }
    }

    [Fact]
    public async Task TokenAcquisitionFailure_AbortsInRequiredAndAuditAndReportsSameCategory()
    {
        var results = new List<ScenarioResult>();

        foreach (var mode in new[] { SiloConnectionAuthenticationMode.Required, SiloConnectionAuthenticationMode.Audit })
        {
            var provider = new DelegateTokenProvider((_, _) =>
                throw new InvalidOperationException("provider-sensitive-sentinel"));
            await using var pair = ConnectionPair.Create(SiloConnectionAuthenticationProtocol.Version2);
            await WriteFrameAsync(
                pair.Peer,
                ResultFrameType,
                [RejectedResult]);
            var logger = new CaptureLogger();
            using var telemetry = new TelemetryCapture();
            var middleware = CreateOutboundMiddleware(mode, provider, logger);
            var downstreamCalls = 0;

            await middleware.OnConnectionAsync(pair.Connection, _ =>
            {
                downstreamCalls++;
                return Task.CompletedTask;
            });

            var peerReceivedFrame = TryReadAvailableFrame(pair.Peer);
            results.Add(CaptureResult(pair.Connection, downstreamCalls, logger, telemetry, peerReceivedFrame));
            Assert.Equal(1, provider.CallCount);
        }

        Assert.All(results, result =>
        {
            Assert.Equal(0, result.DownstreamCalls);
            Assert.NotNull(result.AbortReason);
            Assert.Equal(
                "Orleans connection authentication failed (acquisition_failure).",
                result.AbortReason.Message);
            Assert.False(result.PeerReceivedFrame);
            Assert.Null(result.Feature);
            Assert.Equal("acquisition_failure", Assert.Single(result.FailureLogs).Properties["Category"]);
            Assert.Equal("acquisition_failure", Assert.Single(result.AttemptMetrics).Tags["result"]);
            Assert.DoesNotContain("provider-sensitive-sentinel", result.AllLogText, StringComparison.Ordinal);
        });
        Assert.Equal(
            results[0].FailureLogs.Single().Properties["Category"],
            results[1].FailureLogs.Single().Properties["Category"]);
    }

    [Theory]
    [InlineData("missing-provider", "acquisition_failure")]
    [InlineData("empty-token", "acquisition_failure")]
    [InlineData("oversized-token", "acquisition_failure")]
    [InlineData("invalid-utf8-token", "acquisition_failure")]
    [InlineData("missing-expiration", "acquisition_failure")]
    [InlineData("expired-token", "expiration")]
    public async Task InvalidOutboundCredential_AbortsInRequiredAndAuditWithoutSendingToken(
        string failureCase,
        string expectedCategory)
    {
        var results = new List<ScenarioResult>();

        foreach (var mode in new[] { SiloConnectionAuthenticationMode.Required, SiloConnectionAuthenticationMode.Audit })
        {
            DelegateTokenProvider? provider = failureCase switch
            {
                "missing-provider" => null,
                "empty-token" => new DelegateTokenProvider((_, _) =>
                    ValueTask.FromResult(new SiloConnectionToken(string.Empty, Expiration))),
                "oversized-token" => new DelegateTokenProvider((_, _) =>
                    ValueTask.FromResult(new SiloConnectionToken(new string('x', (16 * 1024) + 1), Expiration))),
                "invalid-utf8-token" => new DelegateTokenProvider((_, _) =>
                    ValueTask.FromResult(new SiloConnectionToken("\uD800", Expiration))),
                "missing-expiration" => new DelegateTokenProvider((_, _) =>
                    ValueTask.FromResult(new SiloConnectionToken("missing-expiration", null))),
                "expired-token" => new DelegateTokenProvider((_, _) =>
                    ValueTask.FromResult(new SiloConnectionToken("insufficient-lifetime", Now.AddMinutes(1)))),
                _ => throw new ArgumentOutOfRangeException(nameof(failureCase)),
            };
            await using var pair = ConnectionPair.Create(SiloConnectionAuthenticationProtocol.Version2);
            await WriteFrameAsync(pair.Peer, ResultFrameType, [RejectedResult]);
            var logger = new CaptureLogger();
            using var telemetry = new TelemetryCapture();
            var middleware = CreateOutboundMiddleware(mode, provider, logger);
            var downstreamCalls = 0;

            await middleware.OnConnectionAsync(pair.Connection, _ =>
            {
                downstreamCalls++;
                return Task.CompletedTask;
            });

            results.Add(CaptureResult(
                pair.Connection,
                downstreamCalls,
                logger,
                telemetry,
                TryReadAvailableFrame(pair.Peer)));
            if (provider is not null)
            {
                Assert.Equal(1, provider.CallCount);
            }
        }

        Assert.All(results, result =>
        {
            Assert.Equal(0, result.DownstreamCalls);
            Assert.Equal(
                $"Orleans connection authentication failed ({expectedCategory}).",
                result.AbortReason?.Message);
            Assert.False(result.PeerReceivedFrame);
            Assert.Null(result.Feature);
            Assert.Equal(expectedCategory, Assert.Single(result.FailureLogs).Properties["Category"]);
            Assert.Equal(expectedCategory, Assert.Single(result.AttemptMetrics).Tags["result"]);
            Assert.DoesNotContain(result.Logs, entry => entry.EventId == 9201);
        });
        Assert.Equal(
            results[0].FailureLogs.Single().Properties["Category"],
            results[1].FailureLogs.Single().Properties["Category"]);
    }

    [Fact]
    public async Task NegotiatedAuthenticationFailure_DiagnosticsAreBounded()
    {
        const string tokenSentinel = "token-SENTINEL-2c65";
        const string tenantSentinel = "tenant-SENTINEL-a764";
        const string issuerSentinel = "issuer-SENTINEL-c639";
        const string audienceSentinel = "audience-SENTINEL-d184";
        const string roleSentinel = "role-SENTINEL-f502";
        var token = string.Join('.', tokenSentinel, tenantSentinel, issuerSentinel, audienceSentinel, roleSentinel);

        foreach (var mode in new[] { SiloConnectionAuthenticationMode.Required, SiloConnectionAuthenticationMode.Audit })
        {
            await using var pair = ConnectionPair.Create(SiloConnectionAuthenticationProtocol.Version2);
            await WriteFrameAsync(
                pair.Peer,
                TokenFrameType,
                Encoding.UTF8.GetBytes(token));
            var logger = new CaptureLogger();
            using var telemetry = new TelemetryCapture();
            var validator = new DelegateTokenValidator((actualToken, _, _) =>
            {
                Assert.Equal(token, actualToken);
                return ValueTask.FromResult(
                    SiloConnectionTokenValidationResult.Fail(SiloConnectionAuthenticationFailure.UnauthorizedCaller));
            });
            var middleware = CreateInboundMiddleware(mode, validator, logger);

            await middleware.OnConnectionAsync(pair.Connection, _ => Task.CompletedTask);

            var failure = Assert.Single(logger.Entries, entry => entry.EventId == 9200);
            Assert.Equal(LogLevel.Warning, failure.Level);
            Assert.Equal("authorization_failure", failure.Properties["Category"]);
            Assert.Equal("authorization_failure", Assert.Single(telemetry.Attempts).Tags["result"]);
            foreach (var sentinel in new[]
                     {
                         tokenSentinel,
                         tenantSentinel,
                         issuerSentinel,
                         audienceSentinel,
                         roleSentinel,
                     })
            {
                Assert.DoesNotContain(sentinel, logger.AllText, StringComparison.Ordinal);
                Assert.DoesNotContain(sentinel, telemetry.AllText, StringComparison.Ordinal);
                Assert.DoesNotContain(sentinel, pair.Connection.AbortReason!.Message, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(SiloConnectionAuthenticationDirection.Inbound)]
    [InlineData(SiloConnectionAuthenticationDirection.Outbound)]
    public async Task Audit_BaselinePeer_FallsBackWithoutClaimingAuthentication(
        SiloConnectionAuthenticationDirection direction)
    {
        await using var pair = ConnectionPair.Create("Orleans1");
        var logger = new CaptureLogger();
        using var telemetry = new TelemetryCapture();
        var middleware = CreateMiddleware(
            SiloConnectionAuthenticationMode.Audit,
            direction,
            provider: null,
            validator: null,
            logger);
        ISiloConnectionAuthenticationFeature? observedFeature = null;
        var downstreamCalls = 0;

        await middleware.OnConnectionAsync(pair.Connection, context =>
        {
            downstreamCalls++;
            observedFeature = context.Features.Get<ISiloConnectionAuthenticationFeature>();
            return Task.CompletedTask;
        });

        Assert.Equal(1, downstreamCalls);
        Assert.Null(pair.Connection.AbortReason);
        var feature = Assert.IsAssignableFrom<ISiloConnectionAuthenticationFeature>(observedFeature);
        Assert.False(feature.AuthenticationAttempted);
        Assert.False(feature.IsAuthenticated);
        Assert.Null(feature.Principal);
        Assert.Null(feature.ExpiresAt);
        Assert.Equal(SiloConnectionAuthenticationFailure.None, feature.Failure);
        Assert.Equal("Orleans1", feature.Protocol);
        var fallback = Assert.Single(logger.Entries, entry => entry.EventId == 9202);
        Assert.Equal(LogLevel.Information, fallback.Level);
        Assert.Empty(telemetry.Attempts);
        Assert.Single(telemetry.ProtocolFallbacks);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId == 9201);
    }

    [Theory]
    [InlineData(SiloConnectionAuthenticationDirection.Inbound)]
    [InlineData(SiloConnectionAuthenticationDirection.Outbound)]
    public async Task Required_BaselinePeer_IsRejected(SiloConnectionAuthenticationDirection direction)
    {
        await using var pair = ConnectionPair.Create("Orleans1");
        var logger = new CaptureLogger();
        using var telemetry = new TelemetryCapture();
        var middleware = CreateMiddleware(
            SiloConnectionAuthenticationMode.Required,
            direction,
            provider: null,
            validator: null,
            logger);
        var downstreamCalls = 0;

        await middleware.OnConnectionAsync(pair.Connection, _ =>
        {
            downstreamCalls++;
            return Task.CompletedTask;
        });

        Assert.Equal(0, downstreamCalls);
        Assert.Equal(
            "Orleans connection authentication failed (tls_policy_error).",
            pair.Connection.AbortReason?.Message);
        Assert.Null(pair.Connection.Features.Get<ISiloConnectionAuthenticationFeature>());
        Assert.Equal("tls_policy_error", Assert.Single(logger.Entries, entry => entry.EventId == 9200).Properties["Category"]);
        Assert.Equal("tls_policy_error", Assert.Single(telemetry.Attempts).Tags["result"]);
        Assert.Empty(telemetry.ProtocolFallbacks);
    }

    [Fact]
    public async Task NegotiatedAuthenticationSuccess_InvokesPipelineAndPreservesPrincipalAndExpiration()
    {
        const string token = "successful-fixed-token";
        const string subject = "silo-success-17";

        foreach (var mode in new[] { SiloConnectionAuthenticationMode.Required, SiloConnectionAuthenticationMode.Audit })
        {
            await using var pair = ConnectionPair.Create(SiloConnectionAuthenticationProtocol.Version2);
            await WriteFrameAsync(
                pair.Peer,
                TokenFrameType,
                Encoding.UTF8.GetBytes(token));
            var principal = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], "fixed-token"));
            SiloConnectionTokenValidationContext? validationContext = null;
            var validator = new DelegateTokenValidator((actualToken, context, _) =>
            {
                Assert.Equal(token, actualToken);
                validationContext = context;
                return ValueTask.FromResult(SiloConnectionTokenValidationResult.Success(principal, Expiration));
            });
            var logger = new CaptureLogger();
            using var telemetry = new TelemetryCapture();
            var middleware = CreateInboundMiddleware(mode, validator, logger);
            ISiloConnectionAuthenticationFeature? observedFeature = null;
            var downstreamCalls = 0;

            await middleware.OnConnectionAsync(pair.Connection, context =>
            {
                downstreamCalls++;
                observedFeature = context.Features.Get<ISiloConnectionAuthenticationFeature>();
                return Task.CompletedTask;
            });
            var (frameType, payload) = await ReadFrameAsync(pair.Peer);

            Assert.Equal(1, downstreamCalls);
            Assert.Null(pair.Connection.AbortReason);
            Assert.Equal(ResultFrameType, frameType);
            Assert.Equal([AuthenticatedResult], payload);
            var feature = Assert.IsAssignableFrom<ISiloConnectionAuthenticationFeature>(observedFeature);
            Assert.True(feature.AuthenticationAttempted);
            Assert.True(feature.IsAuthenticated);
            Assert.Equal(subject, feature.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value);
            Assert.Equal("fixed-token", feature.Principal?.Identity?.AuthenticationType);
            Assert.NotSame(principal, feature.Principal);
            Assert.Equal(Expiration, feature.ExpiresAt);
            Assert.Equal(SiloConnectionAuthenticationFailure.None, feature.Failure);
            Assert.Equal(SiloConnectionAuthenticationProtocol.Version2, feature.Protocol);
            Assert.Equal("phase-3-cluster", validationContext?.ClusterId);
            Assert.Equal(pair.Connection.LocalEndPoint, validationContext?.LocalEndPoint);
            Assert.Equal(pair.Connection.RemoteEndPoint, validationContext?.RemoteEndPoint);
            Assert.Equal("authenticated", Assert.Single(logger.Entries, entry => entry.EventId == 9201).Properties["Result"]);
            Assert.Equal("authenticated", Assert.Single(telemetry.Attempts).Tags["result"]);
        }
    }

    private static async Task<ScenarioResult> RunNegotiatedFailureAsync(
        SiloConnectionAuthenticationMode mode,
        SiloConnectionAuthenticationDirection direction)
    {
        await using var pair = ConnectionPair.Create(SiloConnectionAuthenticationProtocol.Version2);
        var logger = new CaptureLogger();
        using var telemetry = new TelemetryCapture();
        IConnectionMiddleware middleware;

        if (direction == SiloConnectionAuthenticationDirection.Inbound)
        {
            await WriteFrameAsync(
                pair.Peer,
                TokenFrameType,
                Encoding.UTF8.GetBytes("inbound-sensitive-token"));
            middleware = CreateInboundMiddleware(
                mode,
                new DelegateTokenValidator((_, _, _) => ValueTask.FromResult(
                    SiloConnectionTokenValidationResult.Fail(SiloConnectionAuthenticationFailure.UnauthorizedCaller))),
                logger);
        }
        else
        {
            await WriteFrameAsync(
                pair.Peer,
                ResultFrameType,
                [AcceptedUnauthenticatedResult]);
            middleware = CreateOutboundMiddleware(
                mode,
                new DelegateTokenProvider((_, _) =>
                    ValueTask.FromResult(new SiloConnectionToken("fixed-outbound-token", Expiration))),
                logger);
        }

        var downstreamCalls = 0;
        await middleware.OnConnectionAsync(pair.Connection, _ =>
        {
            downstreamCalls++;
            return Task.CompletedTask;
        });
        var (peerFrameType, peerPayload) = await ReadFrameAsync(pair.Peer);

        return CaptureResult(
            pair.Connection,
            downstreamCalls,
            logger,
            telemetry,
            peerReceivedFrame: true,
            peerFrameType,
            peerPayload);
    }

    private static IConnectionMiddleware CreateMiddleware(
        SiloConnectionAuthenticationMode mode,
        SiloConnectionAuthenticationDirection direction,
        ISiloConnectionTokenProvider? provider,
        ISiloConnectionTokenValidator? validator,
        CaptureLogger logger) =>
        direction == SiloConnectionAuthenticationDirection.Inbound
            ? CreateInboundMiddleware(mode, validator, logger)
            : CreateOutboundMiddleware(mode, provider, logger);

    private static TestInboundMiddleware CreateInboundMiddleware(
        SiloConnectionAuthenticationMode mode,
        ISiloConnectionTokenValidator? validator,
        CaptureLogger logger)
    {
        var options = CreateOptions(mode);
        return new TestInboundMiddleware(validator, CreateRegistration(options), logger);
    }

    private static TestOutboundMiddleware CreateOutboundMiddleware(
        SiloConnectionAuthenticationMode mode,
        ISiloConnectionTokenProvider? provider,
        CaptureLogger logger)
    {
        var options = CreateOptions(mode);
        return new TestOutboundMiddleware(provider, CreateRegistration(options), logger);
    }

    private static SiloConnectionAuthenticationRegistration CreateRegistration(
        SiloConnectionAuthenticationOptions options) =>
        new(
            "phase-3",
            ConnectionAuthenticationServiceKeys.Silo,
            options,
            new TlsOptions(),
            hasTokenProvider: true,
            hasTokenValidator: true);

    private static SiloConnectionAuthenticationOptions CreateOptions(SiloConnectionAuthenticationMode mode) => new()
    {
        Mode = mode,
        TimeProvider = new FixedTimeProvider(Now),
        TokenExchangeTimeout = TimeSpan.FromMinutes(5),
        MinimumRemainingTokenLifetime = TimeSpan.FromMinutes(2),
        ExpirationSafetyMargin = TimeSpan.FromSeconds(30),
        ExpirationJitter = TimeSpan.Zero,
    };

    private static ScenarioResult CaptureResult(
        TestConnectionContext connection,
        int downstreamCalls,
        CaptureLogger logger,
        TelemetryCapture telemetry,
        bool peerReceivedFrame,
        byte? peerFrameType = null,
        byte[]? peerPayload = null) =>
        new(
            downstreamCalls,
            connection.AbortReason,
            connection.Features.Get<ISiloConnectionAuthenticationFeature>(),
            logger.Entries.ToArray(),
            telemetry.Measurements.ToArray(),
            peerReceivedFrame,
            peerFrameType,
            peerPayload ?? []);

    private static async ValueTask WriteFrameAsync(ConnectionContext context, byte frameType, byte[] payload) =>
        await ConnectionFrameHelper.WriteFrameAsync(context, frameType, payload, CancellationToken.None);

    private static async ValueTask<(byte FrameType, byte[] Payload)> ReadFrameAsync(ConnectionContext context) =>
        await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None);

    private static bool TryReadAvailableFrame(ConnectionContext context)
    {
        if (!context.Transport.Input.TryRead(out var result))
        {
            return false;
        }

        var hasData = !result.Buffer.IsEmpty;
        context.Transport.Input.AdvanceTo(result.Buffer.End);
        return hasData;
    }

    private sealed class TestInboundMiddleware(
        ISiloConnectionTokenValidator? validator,
        ConnectionAuthenticationRegistration registration,
        ILogger logger)
        : InboundSiloConnectionAuthenticationMiddleware(
            validator,
            registration,
            "phase-3-cluster",
            TestHostApplicationLifetime.Instance,
            logger);

    private sealed class TestOutboundMiddleware(
        ISiloConnectionTokenProvider? provider,
        ConnectionAuthenticationRegistration registration,
        ILogger logger)
        : OutboundSiloConnectionAuthenticationMiddleware(
            provider,
            registration,
            "phase-3-cluster",
            TestHostApplicationLifetime.Instance,
            logger);

    private sealed class DelegateTokenProvider(
        Func<SiloConnectionTokenRequestContext, CancellationToken, ValueTask<SiloConnectionToken>> callback)
        : ISiloConnectionTokenProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<SiloConnectionToken> GetTokenAsync(
            SiloConnectionTokenRequestContext context,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return callback(context, cancellationToken);
        }
    }

    private sealed class DelegateTokenValidator(
        Func<string, SiloConnectionTokenValidationContext, CancellationToken, ValueTask<SiloConnectionTokenValidationResult>> callback)
        : ISiloConnectionTokenValidator
    {
        public ValueTask<SiloConnectionTokenValidationResult> ValidateTokenAsync(
            string token,
            SiloConnectionTokenValidationContext context,
            CancellationToken cancellationToken) =>
            callback(token, context, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            NoOpTimer.Instance;

        private sealed class NoOpTimer : ITimer
        {
            public static readonly NoOpTimer Instance = new();

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public static readonly TestHostApplicationLifetime Instance = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class TestTlsApplicationProtocolFeature(string protocol) : ITlsApplicationProtocolFeature
    {
        public ReadOnlyMemory<byte> ApplicationProtocol { get; } = Encoding.ASCII.GetBytes(protocol);
    }

    private sealed class ConnectionPair : IAsyncDisposable
    {
        private readonly Pipe _connectionToPeer;
        private readonly Pipe _peerToConnection;

        private ConnectionPair(string protocol)
        {
            _connectionToPeer = new Pipe();
            _peerToConnection = new Pipe();
            Connection = new TestConnectionContext(
                new DuplexPipe(_peerToConnection.Reader, _connectionToPeer.Writer),
                "connection");
            Peer = new TestConnectionContext(
                new DuplexPipe(_connectionToPeer.Reader, _peerToConnection.Writer),
                "peer");
            Connection.Features.Set<ITlsApplicationProtocolFeature>(new TestTlsApplicationProtocolFeature(protocol));
        }

        public TestConnectionContext Connection { get; }

        public TestConnectionContext Peer { get; }

        public static ConnectionPair Create(string protocol) => new(protocol);

        public async ValueTask DisposeAsync()
        {
            await _connectionToPeer.Reader.CompleteAsync();
            await _connectionToPeer.Writer.CompleteAsync();
            await _peerToConnection.Reader.CompleteAsync();
            await _peerToConnection.Writer.CompleteAsync();
        }
    }

    private sealed class TestConnectionContext(IDuplexPipe transport, string connectionId) : ConnectionContext
    {
        public override string ConnectionId { get; set; } = connectionId;

        public override IDuplexPipe Transport { get; set; } = transport;

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

        public override EndPoint? LocalEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 11111);

        public override EndPoint? RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 22222);

        public ConnectionAbortedException? AbortReason { get; private set; }

        public override void Abort(ConnectionAbortedException abortReason) => AbortReason = abortReason;
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public string AllText => string.Join(
            Environment.NewLine,
            Entries.SelectMany(entry => entry.Properties.Values.Prepend(entry.Message)));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                {
                    properties[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                }
            }

            Entries.Add(new LogEntry(eventId.Id, logLevel, formatter(state, exception), properties));
        }
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public TelemetryCapture()
        {
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Microsoft.Orleans.Connections.Security"
                    && instrument.Name is "orleans.connections.authentication.attempts"
                        or "orleans.connections.authentication.protocol_fallbacks")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var capturedTags = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    capturedTags[tag.Key] = tag.Value?.ToString() ?? string.Empty;
                }

                Measurements.Add(new MetricMeasurement(instrument.Name, value, capturedTags));
            });
            _listener.Start();
        }

        public List<MetricMeasurement> Measurements { get; } = [];

        public IEnumerable<MetricMeasurement> Attempts =>
            Measurements.Where(measurement =>
                measurement.InstrumentName == "orleans.connections.authentication.attempts");

        public IEnumerable<MetricMeasurement> ProtocolFallbacks =>
            Measurements.Where(measurement =>
                measurement.InstrumentName == "orleans.connections.authentication.protocol_fallbacks");

        public string AllText => string.Join(
            Environment.NewLine,
            Measurements.SelectMany(measurement =>
                measurement.Tags.Select(tag => $"{tag.Key}={tag.Value}")));

        public void Dispose() => _listener.Dispose();
    }

    private sealed record LogEntry(
        int EventId,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, string> Properties);

    private sealed record MetricMeasurement(
        string InstrumentName,
        long Value,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record ScenarioResult(
        int DownstreamCalls,
        ConnectionAbortedException? AbortReason,
        ISiloConnectionAuthenticationFeature? Feature,
        IReadOnlyList<LogEntry> Logs,
        IReadOnlyList<MetricMeasurement> Metrics,
        bool PeerReceivedFrame,
        byte? PeerFrameType,
        byte[] PeerPayload)
    {
        public IEnumerable<LogEntry> FailureLogs => Logs.Where(entry => entry.EventId == 9200);

        public IEnumerable<MetricMeasurement> AttemptMetrics =>
            Metrics.Where(measurement =>
                measurement.InstrumentName == "orleans.connections.authentication.attempts");

        public string AllLogText => string.Join(
            Environment.NewLine,
            Logs.SelectMany(entry => entry.Properties.Values.Prepend(entry.Message)));
    }
}
