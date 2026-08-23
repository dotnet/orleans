using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Messaging;

namespace Orleans.Connections.Security;

internal enum SiloConnectionAuthenticationState
{
    Created,
    ProtocolSelected,
    WorkAdmitted,
    TokenTransferred,
    ResultTransferred,
    Accepted,
    Rejected,
}

internal sealed class SiloConnectionAuthenticationStateMachine
{
    public SiloConnectionAuthenticationState State { get; private set; }

    public void Move(SiloConnectionAuthenticationState expected, SiloConnectionAuthenticationState next)
    {
        if (State != expected)
        {
            throw new InvalidOperationException("Invalid silo connection authentication state transition.");
        }

        State = next;
    }
}

internal abstract class SiloConnectionAuthenticationMiddleware
{
    private static readonly TimeSpan MaxTimerDueTime = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    protected const byte TokenFrameType = 0x01;
    protected const byte ResultFrameType = 0x02;
    protected const byte AuthenticatedResult = 0x01;
    protected const byte AcceptedUnauthenticatedResult = 0x02;
    protected const byte RejectedResult = 0x03;

    private static readonly byte[] BaselineProtocol = Encoding.ASCII.GetBytes("Orleans1");
    private static readonly byte[] AuthenticationProtocol = Encoding.ASCII.GetBytes(SiloConnectionAuthenticationProtocol.Version2);
    private readonly IHostApplicationLifetime _applicationLifetime;

    protected SiloConnectionAuthenticationMiddleware(
        SiloConnectionAuthenticationOptions options,
        string clusterId,
        SiloConnectionAuthenticationTarget target,
        AuthenticationWorkLimiter workLimiter,
        IHostApplicationLifetime applicationLifetime,
        ILogger logger)
    {
        Options = CloneOptions(options);
        ClusterId = clusterId;
        Target = target;
        WorkLimiter = workLimiter;
        _applicationLifetime = applicationLifetime;
        Logger = logger;
    }

    protected SiloConnectionAuthenticationOptions Options { get; }

    protected string ClusterId { get; }

    protected SiloConnectionAuthenticationTarget Target { get; }

    protected AuthenticationWorkLimiter WorkLimiter { get; }

    protected ILogger Logger { get; }

    protected CancellationTokenSource CreateExchangeCancellation(ConnectionContext context, out CancellationTokenSource timeout)
    {
        timeout = new CancellationTokenSource(Options.TokenExchangeTimeout, Options.TimeProvider);
        return CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            context.ConnectionClosed,
            _applicationLifetime.ApplicationStopping);
    }

    protected ProtocolSelection SelectProtocol(ConnectionContext context)
    {
        if (Options.Mode == SiloConnectionAuthenticationMode.Disabled)
        {
            return ProtocolSelection.Disabled;
        }

        var applicationProtocol = context.Features.Get<ITlsApplicationProtocolFeature>()?.ApplicationProtocol;
        if (applicationProtocol is null)
        {
            return ProtocolSelection.Missing;
        }

        if (applicationProtocol.Value.Span.SequenceEqual(AuthenticationProtocol))
        {
            return ProtocolSelection.Authentication;
        }

        if (applicationProtocol.Value.Span.SequenceEqual(BaselineProtocol))
        {
            return ProtocolSelection.Baseline;
        }

        return ProtocolSelection.Unknown;
    }

    protected async Task RunAcceptedAsync(
        ConnectionContext context,
        ConnectionDelegate next,
        SiloConnectionAuthenticationDirection direction,
        SiloConnectionAuthenticationFeature feature,
        long started,
        AuthenticationResultCategory result)
    {
        context.Features.Set<ISiloConnectionAuthenticationFeature>(feature);
        SiloConnectionAuthenticationTelemetry.RecordAttempt(
            started,
            Target,
            direction,
            Options.Mode,
            feature.Protocol,
            result);
        SiloConnectionAuthenticationTelemetry.LogCompleted(
            Logger,
            GetTargetName(Target),
            GetDirectionName(direction),
            SiloConnectionAuthenticationTelemetry.GetModeName(Options.Mode),
            SiloConnectionAuthenticationTelemetry.GetResultName(result));

        if (!feature.IsAuthenticated)
        {
            await next(context);
            return;
        }

        SiloConnectionAuthenticationTelemetry.AddActive(1, Target, direction, Options.Mode, feature.Protocol);
        try
        {
            if (feature.ExpiresAt is not { } expiresAt)
            {
                await next(context);
                return;
            }

            var dueTime = GetExpirationDueTime(context.ConnectionId, expiresAt);
            if (dueTime <= TimeSpan.Zero)
            {
                Abort(context, direction, AuthenticationResultCategory.Expiration);
                return;
            }

            var expirationState = new ExpirationState(context, this, direction, expiresAt);
            using var timer = Options.TimeProvider.CreateTimer(
                static state => ((ExpirationState)state!).Expire(),
                expirationState,
                ClampTimerDueTime(dueTime),
                Timeout.InfiniteTimeSpan);
            expirationState.SetTimer(timer);
            await next(context);
        }
        finally
        {
            SiloConnectionAuthenticationTelemetry.AddActive(-1, Target, direction, Options.Mode, feature.Protocol);
        }
    }

    protected void Abort(
        ConnectionContext context,
        SiloConnectionAuthenticationDirection direction,
        AuthenticationResultCategory category,
        long? started = null)
    {
        if (started is { } start)
        {
            SiloConnectionAuthenticationTelemetry.RecordAttempt(
                start,
                Target,
                direction,
                Options.Mode,
                SiloConnectionAuthenticationProtocol.Version2,
                category);
        }
        else
        {
            SiloConnectionAuthenticationTelemetry.RecordEvent(
                Target,
                direction,
                Options.Mode,
                SiloConnectionAuthenticationProtocol.Version2,
                category);
        }

        SiloConnectionAuthenticationTelemetry.LogFailure(
            Logger,
            GetTargetName(Target),
            GetDirectionName(direction),
            SiloConnectionAuthenticationTelemetry.GetModeName(Options.Mode),
            SiloConnectionAuthenticationTelemetry.GetResultName(category));
        context.Abort(new ConnectionAbortedException(
            $"Orleans connection authentication failed ({SiloConnectionAuthenticationTelemetry.GetResultName(category)})."));
    }

    protected static string GetDirectionName(SiloConnectionAuthenticationDirection direction) =>
        direction == SiloConnectionAuthenticationDirection.Inbound ? "inbound" : "outbound";

    protected static string GetTargetName(SiloConnectionAuthenticationTarget target) =>
        target == SiloConnectionAuthenticationTarget.Silo ? "silo" : "client";

    private static SiloConnectionAuthenticationOptions CloneOptions(SiloConnectionAuthenticationOptions source) => new()
    {
        Mode = source.Mode,
        TokenExchangeTimeout = source.TokenExchangeTimeout,
        MaxTokenSize = source.MaxTokenSize,
        MaxConcurrentInboundAuthentications = source.MaxConcurrentInboundAuthentications,
        MaxConcurrentOutboundAuthentications = source.MaxConcurrentOutboundAuthentications,
        MaxPendingInboundAuthentications = source.MaxPendingInboundAuthentications,
        MaxPendingOutboundAuthentications = source.MaxPendingOutboundAuthentications,
        MinimumRemainingTokenLifetime = source.MinimumRemainingTokenLifetime,
        ExpirationSafetyMargin = source.ExpirationSafetyMargin,
        ExpirationJitter = source.ExpirationJitter,
        AllowNonExpiringCredentials = source.AllowNonExpiringCredentials,
        TargetHost = source.TargetHost,
        TimeProvider = source.TimeProvider,
    };

    private TimeSpan GetExpirationDueTime(string connectionId, DateTimeOffset expiresAt)
    {
        var jitter = GetDeterministicJitter(connectionId, Options.ExpirationJitter);
        return expiresAt - Options.TimeProvider.GetUtcNow() - Options.ExpirationSafetyMargin - jitter;
    }

    private static TimeSpan ClampTimerDueTime(TimeSpan dueTime) =>
        dueTime > MaxTimerDueTime ? MaxTimerDueTime : dueTime;

    private static TimeSpan GetDeterministicJitter(string value, TimeSpan maximum)
    {
        if (maximum <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        ulong hash = 14_695_981_039_346_656_037;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 1_099_511_628_211;
        }

        return TimeSpan.FromTicks((long)(hash % ((ulong)maximum.Ticks + 1)));
    }

    protected enum ProtocolSelection
    {
        Disabled,
        Missing,
        Baseline,
        Authentication,
        Unknown,
    }

    private sealed class ExpirationState
    {
        private readonly ConnectionContext _context;
        private readonly SiloConnectionAuthenticationMiddleware _middleware;
        private readonly SiloConnectionAuthenticationDirection _direction;
        private readonly DateTimeOffset _expiresAt;
        private readonly object _lock = new();
        private ITimer? _timer;
        private bool _rescheduleRequested;
        private int _expired;

        public ExpirationState(
            ConnectionContext context,
            SiloConnectionAuthenticationMiddleware middleware,
            SiloConnectionAuthenticationDirection direction,
            DateTimeOffset expiresAt)
        {
            _context = context;
            _middleware = middleware;
            _direction = direction;
            _expiresAt = expiresAt;
        }

        public void SetTimer(ITimer timer)
        {
            lock (_lock)
            {
                _timer = timer;
                if (_rescheduleRequested)
                {
                    _rescheduleRequested = false;
                    RearmOrExpire();
                }
            }
        }

        public void Expire()
        {
            lock (_lock)
            {
                if (_timer is null)
                {
                    _rescheduleRequested = true;
                    return;
                }

                RearmOrExpire();
            }
        }

        private void RearmOrExpire()
        {
            var dueTime = _middleware.GetExpirationDueTime(_context.ConnectionId, _expiresAt);
            if (dueTime > TimeSpan.Zero)
            {
                _timer!.Change(ClampTimerDueTime(dueTime), Timeout.InfiniteTimeSpan);
                return;
            }

            if (Interlocked.Exchange(ref _expired, 1) == 0)
            {
                _middleware.Abort(_context, _direction, AuthenticationResultCategory.Expiration);
            }
        }
    }
}

internal class InboundSiloConnectionAuthenticationMiddleware : SiloConnectionAuthenticationMiddleware, IConnectionMiddleware
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ISiloConnectionTokenValidator? _validator;

    public InboundSiloConnectionAuthenticationMiddleware(
        IServiceProvider serviceProvider,
        SiloConnectionAuthenticationRegistration registration,
        IOptions<ClusterOptions> clusterOptions,
        IHostApplicationLifetime applicationLifetime,
        ILogger<InboundSiloConnectionAuthenticationMiddleware> logger)
        : this(
            serviceProvider.GetServices<ISiloConnectionTokenValidator>().SingleOrDefault(),
            registration,
            clusterOptions.Value.ClusterId,
            applicationLifetime,
            logger)
    {
    }

    protected InboundSiloConnectionAuthenticationMiddleware(
        ISiloConnectionTokenValidator? validator,
        ConnectionAuthenticationRegistration registration,
        string clusterId,
        IHostApplicationLifetime applicationLifetime,
        ILogger logger)
        : base(
            registration.Options,
            clusterId,
            registration.Target,
            registration.WorkLimiter,
            applicationLifetime,
            logger)
    {
        _validator = validator;
    }

    public async Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next)
    {
        var direction = SiloConnectionAuthenticationDirection.Inbound;
        var selection = SelectProtocol(context);
        if (selection == ProtocolSelection.Disabled)
        {
            await next(context);
            return;
        }

        if (selection == ProtocolSelection.Baseline && Options.Mode == SiloConnectionAuthenticationMode.Audit)
        {
            context.Features.Set<ISiloConnectionAuthenticationFeature>(new SiloConnectionAuthenticationFeature(
                false,
                false,
                null,
                null,
                SiloConnectionAuthenticationFailure.None,
                "Orleans1"));
            SiloConnectionAuthenticationTelemetry.RecordFallback(Target, direction, Options.Mode);
            SiloConnectionAuthenticationTelemetry.LogFallback(Logger, GetTargetName(Target), GetDirectionName(direction));
            await next(context);
            return;
        }

        var started = SiloConnectionAuthenticationTelemetry.Start();
        if (selection != ProtocolSelection.Authentication)
        {
            Abort(context, direction, AuthenticationResultCategory.TlsPolicyError, started);
            return;
        }

        var state = new SiloConnectionAuthenticationStateMachine();
        state.Move(SiloConnectionAuthenticationState.Created, SiloConnectionAuthenticationState.ProtocolSelected);

        using var linked = CreateExchangeCancellation(context, out var timeout);
        using (timeout)
        {
            IDisposable? admission = null;
            try
            {
                admission = await WorkLimiter.TryAcquireAsync(direction, linked.Token);
                if (admission is null)
                {
                    Abort(context, direction, AuthenticationResultCategory.Overload, started);
                    return;
                }

                state.Move(SiloConnectionAuthenticationState.ProtocolSelected, SiloConnectionAuthenticationState.WorkAdmitted);
                using (admission)
                {
                    var (frameType, tokenBytes) = await ConnectionFrameHelper.ReadFrameAsync(
                        context,
                        linked.Token,
                        Options.MaxTokenSize + 1);
                    if (frameType != TokenFrameType)
                    {
                        Abort(context, direction, AuthenticationResultCategory.ProtocolError, started);
                        return;
                    }

                    string token;
                    try
                    {
                        token = StrictUtf8.GetString(tokenBytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        Abort(context, direction, AuthenticationResultCategory.ProtocolError, started);
                        return;
                    }

                    state.Move(SiloConnectionAuthenticationState.WorkAdmitted, SiloConnectionAuthenticationState.TokenTransferred);
                    var validation = await ValidateAsync(context, token, linked.Token);
                    var isAuthenticated = TryNormalizeValidation(validation, out var principal, out var expiresAt, out var failure);
                    var resultCode = isAuthenticated ? AuthenticatedResult : RejectedResult;

                    await ConnectionFrameHelper.WriteFrameAsync(
                        context,
                        ResultFrameType,
                        [resultCode],
                        linked.Token);
                    state.Move(SiloConnectionAuthenticationState.TokenTransferred, SiloConnectionAuthenticationState.ResultTransferred);

                    if (resultCode == RejectedResult)
                    {
                        state.Move(SiloConnectionAuthenticationState.ResultTransferred, SiloConnectionAuthenticationState.Rejected);
                        Abort(context, direction, GetValidationCategory(failure), started);
                        return;
                    }

                    state.Move(SiloConnectionAuthenticationState.ResultTransferred, SiloConnectionAuthenticationState.Accepted);
                    var feature = new SiloConnectionAuthenticationFeature(
                        true,
                        isAuthenticated,
                        principal,
                        expiresAt,
                        failure,
                        SiloConnectionAuthenticationProtocol.Version2);
                    admission.Dispose();
                    linked.Dispose();
                    timeout.Dispose();
                    await RunAcceptedAsync(
                        context,
                        next,
                        direction,
                        feature,
                        started,
                        AuthenticationResultCategory.Authenticated);
                }
            }
            catch (OperationCanceledException) when (state.State != SiloConnectionAuthenticationState.Accepted)
            {
                Abort(
                    context,
                    direction,
                    timeout.IsCancellationRequested ? AuthenticationResultCategory.Timeout : AuthenticationResultCategory.ProtocolError,
                    started);
            }
            catch (InvalidOperationException) when (state.State != SiloConnectionAuthenticationState.Accepted)
            {
                Abort(context, direction, AuthenticationResultCategory.ProtocolError, started);
            }
        }
    }

    private async ValueTask<SiloConnectionTokenValidationResult> ValidateAsync(
        ConnectionContext context,
        string token,
        CancellationToken cancellationToken)
    {
        if (token.Length == 0)
        {
            return SiloConnectionTokenValidationResult.Fail(SiloConnectionAuthenticationFailure.MissingToken);
        }

        if (_validator is null)
        {
            return SiloConnectionTokenValidationResult.Fail(SiloConnectionAuthenticationFailure.ProviderUnavailable);
        }

        try
        {
            return await _validator.ValidateTokenAsync(
                token,
                new SiloConnectionTokenValidationContext(
                    ClusterId,
                    Target,
                    context.LocalEndPoint,
                    context.RemoteEndPoint),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return SiloConnectionTokenValidationResult.Fail(SiloConnectionAuthenticationFailure.ValidationError);
        }
    }

    private bool TryNormalizeValidation(
        SiloConnectionTokenValidationResult validation,
        out ClaimsPrincipal? principal,
        out DateTimeOffset? expiresAt,
        out SiloConnectionAuthenticationFailure failure)
    {
        principal = null;
        expiresAt = null;
        failure = validation.Failure;
        if (!validation.Succeeded)
        {
            if (failure == SiloConnectionAuthenticationFailure.None)
            {
                failure = SiloConnectionAuthenticationFailure.ValidationError;
            }

            return false;
        }

        principal = validation.Principal;
        expiresAt = validation.ExpiresAt;
        if (principal is null)
        {
            failure = SiloConnectionAuthenticationFailure.ValidationError;
            return false;
        }

        if (expiresAt is null)
        {
            if (Options.AllowNonExpiringCredentials)
            {
                failure = SiloConnectionAuthenticationFailure.None;
                return true;
            }

            failure = SiloConnectionAuthenticationFailure.ValidationError;
            return false;
        }

        if (expiresAt <= Options.TimeProvider.GetUtcNow() + Options.MinimumRemainingTokenLifetime)
        {
            principal = null;
            expiresAt = null;
            failure = SiloConnectionAuthenticationFailure.ExpiredToken;
            return false;
        }

        failure = SiloConnectionAuthenticationFailure.None;
        return true;
    }

    private static AuthenticationResultCategory GetValidationCategory(SiloConnectionAuthenticationFailure failure) => failure switch
    {
        SiloConnectionAuthenticationFailure.UnauthorizedCaller => AuthenticationResultCategory.AuthorizationFailure,
        SiloConnectionAuthenticationFailure.ProviderUnavailable or
        SiloConnectionAuthenticationFailure.ValidationError => AuthenticationResultCategory.ValidationFailure,
        SiloConnectionAuthenticationFailure.ExpiredToken => AuthenticationResultCategory.Expiration,
        _ => AuthenticationResultCategory.Rejected,
    };
}

internal sealed class InboundGatewayConnectionAuthenticationMiddleware : InboundSiloConnectionAuthenticationMiddleware
{
    public InboundGatewayConnectionAuthenticationMiddleware(
        IServiceProvider serviceProvider,
        GatewayConnectionAuthenticationRegistration registration,
        IOptions<ClusterOptions> clusterOptions,
        IHostApplicationLifetime applicationLifetime,
        ILogger<InboundGatewayConnectionAuthenticationMiddleware> logger)
        : base(
            serviceProvider.GetKeyedService<ISiloConnectionTokenValidator>(registration.ServiceKey),
            registration,
            clusterOptions.Value.ClusterId,
            applicationLifetime,
            logger)
    {
    }
}

internal class OutboundSiloConnectionAuthenticationMiddleware : SiloConnectionAuthenticationMiddleware, IConnectionMiddleware
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ISiloConnectionTokenProvider? _provider;

    public OutboundSiloConnectionAuthenticationMiddleware(
        IServiceProvider serviceProvider,
        SiloConnectionAuthenticationRegistration registration,
        IOptions<ClusterOptions> clusterOptions,
        IHostApplicationLifetime applicationLifetime,
        ILogger<OutboundSiloConnectionAuthenticationMiddleware> logger)
        : this(
            serviceProvider.GetServices<ISiloConnectionTokenProvider>().SingleOrDefault(),
            registration,
            clusterOptions.Value.ClusterId,
            applicationLifetime,
            logger)
    {
    }

    protected OutboundSiloConnectionAuthenticationMiddleware(
        ISiloConnectionTokenProvider? provider,
        ConnectionAuthenticationRegistration registration,
        string clusterId,
        IHostApplicationLifetime applicationLifetime,
        ILogger logger)
        : base(
            registration.Options,
            clusterId,
            registration.Target,
            registration.WorkLimiter,
            applicationLifetime,
            logger)
    {
        _provider = provider;
    }

    public async Task OnConnectionAsync(ConnectionContext context, ConnectionDelegate next)
    {
        var direction = SiloConnectionAuthenticationDirection.Outbound;
        var selection = SelectProtocol(context);
        if (selection == ProtocolSelection.Disabled)
        {
            await next(context);
            return;
        }

        if (selection == ProtocolSelection.Baseline && Options.Mode == SiloConnectionAuthenticationMode.Audit)
        {
            context.Features.Set<ISiloConnectionAuthenticationFeature>(new SiloConnectionAuthenticationFeature(
                false,
                false,
                null,
                null,
                SiloConnectionAuthenticationFailure.None,
                "Orleans1"));
            SiloConnectionAuthenticationTelemetry.RecordFallback(Target, direction, Options.Mode);
            SiloConnectionAuthenticationTelemetry.LogFallback(Logger, GetTargetName(Target), GetDirectionName(direction));
            await next(context);
            return;
        }

        var started = SiloConnectionAuthenticationTelemetry.Start();
        if (selection != ProtocolSelection.Authentication)
        {
            Abort(context, direction, AuthenticationResultCategory.TlsPolicyError, started);
            return;
        }

        var state = new SiloConnectionAuthenticationStateMachine();
        state.Move(SiloConnectionAuthenticationState.Created, SiloConnectionAuthenticationState.ProtocolSelected);

        using var linked = CreateExchangeCancellation(context, out var timeout);
        using (timeout)
        {
            try
            {
                var admission = await WorkLimiter.TryAcquireAsync(direction, linked.Token);
                if (admission is null)
                {
                    Abort(context, direction, AuthenticationResultCategory.Overload, started);
                    return;
                }

                state.Move(SiloConnectionAuthenticationState.ProtocolSelected, SiloConnectionAuthenticationState.WorkAdmitted);
                using (admission)
                {
                    var (payload, expiresAt, localFailure) = await GetTokenPayloadAsync(context, linked.Token);
                    if (payload is null)
                    {
                        Abort(context, direction, GetAcquisitionCategory(localFailure), started);
                        return;
                    }

                    await ConnectionFrameHelper.WriteFrameAsync(context, TokenFrameType, payload, linked.Token);
                    state.Move(SiloConnectionAuthenticationState.WorkAdmitted, SiloConnectionAuthenticationState.TokenTransferred);

                    var (frameType, resultPayload) = await ConnectionFrameHelper.ReadFrameAsync(context, linked.Token, 2);
                    if (frameType != ResultFrameType || resultPayload.Length != 1)
                    {
                        Abort(context, direction, AuthenticationResultCategory.ProtocolError, started);
                        return;
                    }

                    state.Move(SiloConnectionAuthenticationState.TokenTransferred, SiloConnectionAuthenticationState.ResultTransferred);
                    switch (resultPayload[0])
                    {
                        case AuthenticatedResult:
                            state.Move(SiloConnectionAuthenticationState.ResultTransferred, SiloConnectionAuthenticationState.Accepted);
                            admission.Dispose();
                            linked.Dispose();
                            timeout.Dispose();
                            await RunAcceptedAsync(
                                context,
                                next,
                                direction,
                                new SiloConnectionAuthenticationFeature(
                                    true,
                                    true,
                                    null,
                                    expiresAt,
                                    SiloConnectionAuthenticationFailure.None,
                                    SiloConnectionAuthenticationProtocol.Version2),
                                started,
                                AuthenticationResultCategory.Authenticated);
                            return;
                        case RejectedResult:
                        case AcceptedUnauthenticatedResult:
                            state.Move(SiloConnectionAuthenticationState.ResultTransferred, SiloConnectionAuthenticationState.Rejected);
                            Abort(context, direction, AuthenticationResultCategory.Rejected, started);
                            return;
                        default:
                            Abort(context, direction, AuthenticationResultCategory.ProtocolError, started);
                            return;
                    }
                }
            }
            catch (OperationCanceledException) when (state.State != SiloConnectionAuthenticationState.Accepted)
            {
                Abort(
                    context,
                    direction,
                    timeout.IsCancellationRequested ? AuthenticationResultCategory.Timeout : AuthenticationResultCategory.ProtocolError,
                    started);
            }
            catch (InvalidOperationException) when (state.State != SiloConnectionAuthenticationState.Accepted)
            {
                Abort(context, direction, AuthenticationResultCategory.ProtocolError, started);
            }
        }
    }

    private async ValueTask<(byte[]? Payload, DateTimeOffset? ExpiresAt, SiloConnectionAuthenticationFailure Failure)> GetTokenPayloadAsync(
        ConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (_provider is null)
        {
            return (null, null, SiloConnectionAuthenticationFailure.ProviderUnavailable);
        }

        SiloConnectionToken token;
        try
        {
            token = await _provider.GetTokenAsync(
                new SiloConnectionTokenRequestContext(
                    ClusterId,
                    Target,
                    context.LocalEndPoint,
                    context.RemoteEndPoint),
                cancellationToken);
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, null, SiloConnectionAuthenticationFailure.ProviderUnavailable);
        }

        var value = token.Value ?? string.Empty;
        byte[] payload;
        try
        {
            if (StrictUtf8.GetByteCount(value) > Options.MaxTokenSize)
            {
                return (null, null, SiloConnectionAuthenticationFailure.InvalidToken);
            }

            payload = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            return (null, null, SiloConnectionAuthenticationFailure.InvalidToken);
        }

        if (payload.Length == 0)
        {
            return (null, null, SiloConnectionAuthenticationFailure.MissingToken);
        }

        if (token.ExpiresAt is null && !Options.AllowNonExpiringCredentials)
        {
            return (null, null, SiloConnectionAuthenticationFailure.ValidationError);
        }

        if (token.ExpiresAt is { } expiresAt
            && expiresAt <= Options.TimeProvider.GetUtcNow() + Options.MinimumRemainingTokenLifetime)
        {
            return (null, null, SiloConnectionAuthenticationFailure.ExpiredToken);
        }

        return (payload, token.ExpiresAt, SiloConnectionAuthenticationFailure.None);
    }

    private static AuthenticationResultCategory GetAcquisitionCategory(SiloConnectionAuthenticationFailure failure) =>
        failure == SiloConnectionAuthenticationFailure.ExpiredToken
            ? AuthenticationResultCategory.Expiration
            : AuthenticationResultCategory.AcquisitionFailure;
}

internal sealed class OutboundClientConnectionAuthenticationMiddleware : OutboundSiloConnectionAuthenticationMiddleware
{
    public OutboundClientConnectionAuthenticationMiddleware(
        IServiceProvider serviceProvider,
        ClientConnectionAuthenticationRegistration registration,
        IOptions<ClusterOptions> clusterOptions,
        IHostApplicationLifetime applicationLifetime,
        ILogger<OutboundClientConnectionAuthenticationMiddleware> logger)
        : base(
            serviceProvider.GetKeyedService<ISiloConnectionTokenProvider>(registration.ServiceKey),
            registration,
            clusterOptions.Value.ClusterId,
            applicationLifetime,
            logger)
    {
    }
}
