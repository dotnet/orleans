using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging;
using Orleans.Durable.Tasks;
using Orleans.Serialization;

namespace DurableWorkflows;

public sealed class ExternalEffectGrain : DurableGrain, IExternalEffectGrain, IDurableJobHandler
{
    private readonly IDurableDictionary<string, EffectRecord> _effects;
    private readonly ILocalDurableJobManager _jobs;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<ExternalEffectOptions> _options;
    private readonly IOutbox _outbox;
    private readonly MessageType<EffectRecord> _outcomes;
    private readonly Dictionary<string, CancellationTokenSource> _active = [];

    public ExternalEffectGrain(ILocalDurableJobManager jobs,
        [FromKeyedServices(ExternalEffectOptions.ClockName)] TimeProvider clock, IOptionsMonitor<ExternalEffectOptions> options,
        IOutbox outbox, Serializer serializer)
    {
        _jobs = jobs;
        _clock = clock;
        _options = options;
        _outbox = outbox;
        _outcomes = new("external-effect-outcome", serializer);
        _effects = StateManager.GetOrAddDictionary<string, EffectRecord>("external-effects");
    }

    public async Task<EffectRecord> SubmitAsync(EffectIdentity identity, EffectRequest request)
    {
        ValidateIdentity(identity);
        ValidateRequest(request);
        var key = Key(identity);
        if (_effects.TryGetValue(key, out var existing))
        {
            if (existing.Request != request)
            {
                throw new InvalidOperationException("The effect identity already names a different request.");
            }

            await WriteStateAsync();
            return existing;
        }

        var now = _clock.GetUtcNow();
        var policy = _options.CurrentValue.Capture(request.Kind, now);
        ValidatePolicy(policy, request.Kind, now);
        var nextPolicy = request.OnSuccess is { } nextRequest ? _options.CurrentValue.Capture(nextRequest.Kind, now) : null;
        if (nextPolicy is not null)
        {
            ValidatePolicy(nextPolicy, request.OnSuccess!.Kind, now);
        }
        // The extra wake-up can be orphaned by a failed owner write. Its handler reads only committed owner state.
        await ArmAsync(now);
        var effect = new EffectRecord
        {
            Identity = identity,
            Request = request,
            ProviderKey = key,
            Policy = policy,
            Status = EffectStatus.Pending,
            NextActionAt = now,
            NextAction = "dispatch",
            ResolutionOwner = $"effect-operator/{this.GetPrimaryKeyString()}",
            OnSuccessPolicy = nextPolicy
        };
        _effects[key] = effect;
        await WriteStateAsync();
        return effect;
    }

    public async Task<EffectRecord?> GetAsync(EffectIdentity identity)
    {
        // Capture before the write: an interleaved decision after its capture belongs to a later acknowledgement.
        var result = _effects.TryGetValue(Key(identity), out var effect) ? effect : null;
        await WriteStateAsync();
        return result;
    }

    public Task RunDueAsync() => RunDueCoreAsync(CancellationToken.None);

    private async Task RunDueCoreAsync(CancellationToken attemptCancellationToken)
    {
        foreach (var key in _effects.Keys.ToArray())
        {
            var effect = _effects[key];
            if (!effect.IsCompleted && effect.NextActionAt <= _clock.GetUtcNow() && !_active.ContainsKey(key))
            {
                attemptCancellationToken.ThrowIfCancellationRequested();
                await AdvanceAsync(effect, attemptCancellationToken);
            }
        }
    }

    public Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken) => RunDueCoreAsync(attemptCancellationToken);

    public async Task<EffectRecord> CancelAsync(EffectIdentity identity, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var effect = Required(identity);
        if (effect.IsCompleted || effect.CancellationRequested)
        {
            await WriteStateAsync();
            return effect;
        }

        var next = effect with
        {
            CancellationRequested = true,
            CancellationReason = reason,
            Status = effect.MayHaveApplied ? EffectStatus.Reconciling : EffectStatus.Canceled,
            Evidence = reason,
            NextAction = effect.MayHaveApplied ? "lookup-or-operator-decision" : "completed",
            NextActionAt = _clock.GetUtcNow(),
            Revision = effect.Revision + 1
        };
        var saved = await SaveAsync(next);
        if (_active.TryGetValue(effect.ProviderKey, out var attempt))
        {
            attempt.Cancel();
        }

        return saved;
    }

    public async Task<EffectRecord> ResolveAsync(EffectIdentity identity, long expectedRevision, ProviderOutcome outcome, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        var effect = Required(identity);
        CheckRevision(effect, expectedRevision);
        if (effect.IsCompleted || outcome is ProviderOutcome.Unknown || !Enum.IsDefined(outcome))
        {
            throw new InvalidOperationException("Resolve an outstanding effect using provider evidence or an authorized business decision.");
        }

        // A decision closes dispatch for this identity. A deliberate resend is a separately authorized effect.
        var next = Complete(effect, outcome, $"operator: {evidence}");
        var saved = await SaveAsync(next);
        if (_active.TryGetValue(effect.ProviderKey, out var attempt))
        {
            attempt.Cancel();
        }

        return saved;
    }

    public async Task<EffectRecord> AuthorizeFollowUpAsync(EffectIdentity original, EffectIdentity followUp, string authorization, EffectRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorization);
        ValidateIdentity(followUp);
        var effect = Required(original);
        var followUpRequest = request ?? effect.Request;
        ValidateRequest(followUpRequest);
        var newKey = Key(followUp);
        if (newKey == effect.ProviderKey)
        {
            throw new InvalidOperationException("A follow-up requires a new logical effect identity.");
        }

        if (effect.FollowUpEffectKey is { } recorded)
        {
            if (recorded != newKey || _effects[recorded].Authorization != authorization || _effects[recorded].Request != followUpRequest)
            {
                throw new InvalidOperationException("The original already has a different authorized follow-up.");
            }

            await WriteStateAsync();
            return _effects[recorded];
        }

        ThrowIfFollowUpBlocked();
        var now = _clock.GetUtcNow();
        var policy = _options.CurrentValue.Capture(followUpRequest.Kind, now);
        ValidatePolicy(policy, followUpRequest.Kind, now);
        var nextPolicy = followUpRequest.OnSuccess is { } nextRequest ? _options.CurrentValue.Capture(nextRequest.Kind, now) : null;
        if (nextPolicy is not null)
        {
            ValidatePolicy(nextPolicy, followUpRequest.OnSuccess!.Kind, now);
        }
        await ArmAsync(now);
        // A due job can interleave with scheduling. An attempt which became active, or which already recorded a newer
        // revision, still owns the original's outcome, so authorization waits until that attempt settles.
        CheckRevision(_effects[effect.ProviderKey], effect.Revision);
        ThrowIfFollowUpBlocked();
        var next = new EffectRecord
        {
            Identity = followUp,
            Request = followUpRequest,
            ProviderKey = newKey,
            Policy = policy,
            Status = EffectStatus.Pending,
            NextActionAt = now,
            NextAction = "dispatch",
            ResolutionOwner = effect.ResolutionOwner,
            RelatedEffectKey = effect.ProviderKey,
            Authorization = authorization,
            OnSuccessPolicy = nextPolicy
        };
        // The original retains uncertainty and its evidence. Authorization transfers the next business action, rather
        // than inventing a provider outcome. Cancellation prevents another dispatch under the original identity.
        _effects[effect.ProviderKey] = effect with
        {
            CancellationRequested = true,
            FollowUpEffectKey = newKey,
            Revision = effect.Revision + 1,
            CancellationReason = $"follow-up authorized: {authorization}",
            NextAction = effect.IsCompleted ? "completed" : "lookup-original-after-authorized-follow-up"
        };
        _effects[newKey] = next;
        await WriteStateAsync();
        return next;

        void ThrowIfFollowUpBlocked()
        {
            if (_effects.ContainsKey(newKey) || _active.ContainsKey(effect.ProviderKey))
            {
                throw new InvalidOperationException("Use an unused identity and settle the active attempt before authorizing a follow-up.");
            }
        }
    }

    public async Task<EffectRecord> ChangePolicyAsync(EffectIdentity identity, long expectedRevision, EffectPolicy policy, string authorization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorization);
        var effect = Required(identity);
        CheckRevision(effect, expectedRevision);
        ValidatePolicy(policy, effect.Request.Kind, _clock.GetUtcNow());
        if (effect.IsCompleted || effect.Attempts.Length != 0 || _active.ContainsKey(effect.ProviderKey))
        {
            throw new InvalidOperationException("Policy changes require a pending effect with no attempted dispatch.");
        }

        var next = effect with { Policy = policy, Evidence = $"policy authorized: {authorization}", Revision = effect.Revision + 1 };
        return await SaveAsync(next);
    }

    private async Task AdvanceAsync(EffectRecord effect, CancellationToken attemptCancellationToken)
    {
        var key = effect.ProviderKey;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellationToken);
        _active.Add(key, cancellation);
        try
        {
            if (!Supported(effect.Policy, effect.Request.Kind))
            {
                await SaveAsync(effect with
                {
                    Status = EffectStatus.Blocked,
                    NextAction = "operator-upgrade-or-change-policy",
                    Evidence = "Stored execution policy is unsupported; dispatch is blocked.",
                    NextActionAt = _clock.GetUtcNow() + TimeSpan.FromMinutes(1),
                    Revision = effect.Revision + 1
                });
                return;
            }

            var now = _clock.GetUtcNow();
            if (effect.MayHaveApplied && (effect.Policy.Execution is EffectExecution.ReconcileOnInterruption
                || effect.CancellationRequested || !CanDispatch(effect, now)))
            {
                await ReconcileAsync(effect, cancellation.Token);
                return;
            }

            if (effect.CancellationRequested || !CanDispatch(effect, now))
            {
                await SaveAsync(Complete(effect, ProviderOutcome.ProvenNoEffect, "Dispatch stopped before any potentially applied attempt."));
                return;
            }

            var attempt = new EffectAttempt(Guid.NewGuid(), now, "Attempt may begin; absence of a response is uncertain.");
            var marked = effect with
            {
                Status = EffectStatus.Attempting,
                MayHaveApplied = true,
                Attempts = [.. effect.Attempts, attempt],
                Revision = effect.Revision + 1,
                NextActionAt = now + effect.Policy.AttemptTimeout + effect.Policy.Backoff,
                NextAction = effect.Policy.Execution is EffectExecution.RetrySafe ? "retry-same-key-or-lookup" : "lookup-or-operator-decision"
            };
            await SaveAsync(marked);
            // Cancel/Resolve can interleave with storage acknowledgement. Only the current revision may dispatch.
            if (!ReferenceEquals(_effects[key], marked) || cancellation.IsCancellationRequested)
            {
                return;
            }

            if (!FitsSafetyWindow(marked.Policy, _clock.GetUtcNow()))
            {
                var withheldAttempts = marked.Attempts.ToArray();
                withheldAttempts[^1] = attempt with { Outcome = ProviderOutcome.ProvenNoEffect, Evidence = "Deadline elapsed while acknowledging the attempt marker; dispatch was withheld." };
                var withheld = marked with { Attempts = withheldAttempts };
                await SaveAsync(effect.MayHaveApplied
                    ? withheld with
                    {
                        Status = EffectStatus.Reconciling,
                        Evidence = withheldAttempts[^1].Evidence,
                        Revision = withheld.Revision + 1,
                        NextAction = "provider-lookup-or-operator-decision"
                    }
                    : Complete(withheld, ProviderOutcome.ProvenNoEffect, withheldAttempts[^1].Evidence));
                return;
            }

            cancellation.CancelAfter(effect.Policy.AttemptTimeout);
            ProviderResult result;
            try
            {
                result = await Provider(marked).DispatchAsync(key, marked.Request, attempt.Id, cancellation.Token)
                    .WaitAsync(effect.Policy.AttemptTimeout, cancellation.Token);
            }
            catch (Exception exception)
            {
                // Timeout, cancellation and arbitrary provider/transport errors are evidence of uncertainty.
                result = new(ProviderOutcome.Unknown, $"{exception.GetType().Name}: {exception.Message}");
            }

            if (!ReferenceEquals(_effects[key], marked))
            {
                return;
            }

            var evidence = marked.Attempts.ToArray();
            evidence[^1] = attempt with { Evidence = result.Evidence, Outcome = result.Outcome };
            marked = marked with { Attempts = evidence };
            var next = result.Outcome is ProviderOutcome.Applied or ProviderOutcome.Rejected
                ? Complete(marked, result.Outcome, result.Evidence)
                : marked with
                {
                    Status = result.Outcome is ProviderOutcome.ProvenNoEffect && !effect.MayHaveApplied ? EffectStatus.Pending : EffectStatus.Reconciling,
                    MayHaveApplied = result.Outcome is not ProviderOutcome.ProvenNoEffect || effect.MayHaveApplied,
                    Evidence = result.Evidence,
                    NextActionAt = _clock.GetUtcNow() + marked.Policy.Backoff,
                    Revision = marked.Revision + 1
                };
            await SaveAsync(next);
        }
        finally
        {
            _active.Remove(key);
        }
    }

    private async Task ReconcileAsync(EffectRecord effect, CancellationToken cancellationToken)
    {
        // Schedule the next action before issuing the query, so an unavailable provider leaves a responsible owner.
        var checking = effect with
        {
            Status = EffectStatus.Reconciling,
            Revision = effect.Revision + 1,
            Evidence = effect.Status is EffectStatus.Attempting ? "Interrupted after attempt marker; dispatch may have occurred." : effect.Evidence,
            NextActionAt = _clock.GetUtcNow() + effect.Policy.RecheckInterval,
            NextAction = "provider-lookup-or-operator-decision"
        };
        await SaveAsync(checking);
        if (!ReferenceEquals(_effects[effect.ProviderKey], checking))
        {
            return;
        }

        ProviderResult result;
        try
        {
            result = await Provider(effect).LookupAsync(effect.ProviderKey, effect.Request, cancellationToken)
                .WaitAsync(effect.Policy.AttemptTimeout, cancellationToken);
        }
        catch (Exception exception)
        {
            result = new(ProviderOutcome.Unknown, $"Lookup failed: {exception.GetType().Name}: {exception.Message}");
        }

        if (!ReferenceEquals(_effects[effect.ProviderKey], checking))
        {
            return;
        }

        await SaveAsync(result.Outcome is ProviderOutcome.Unknown
            ? checking with
            {
                Evidence = result.Evidence,
                Revision = checking.Revision + 1,
                NextActionAt = _clock.GetUtcNow() + effect.Policy.RecheckInterval
            }
            : Complete(checking, result.Outcome, $"provider lookup: {result.Evidence}"));
    }

    private async Task<EffectRecord> SaveAsync(EffectRecord next)
    {
        EffectRecord? outgoing = null;
        if (next.Status is EffectStatus.Succeeded && next.Request.OnSuccess is { } request)
        {
            var identity = next.Identity with { OperationId = $"{next.Identity.OperationId}/on-success" };
            var key = Key(identity);
            var now = _clock.GetUtcNow();
            var policy = next.OnSuccessPolicy ?? throw new InvalidOperationException("The on-success intent requires its captured policy.");
            await ArmAsync(now);
            outgoing = new()
            {
                Identity = identity,
                Request = request,
                ProviderKey = key,
                Policy = policy,
                Status = EffectStatus.Pending,
                NextActionAt = now,
                NextAction = "dispatch",
                ResolutionOwner = next.ResolutionOwner,
                RelatedEffectKey = next.ProviderKey,
                Authorization = "original request on-success intent"
            };
            next = next with { NextEffectKey = key };
        }

        if (!next.IsCompleted)
        {
            await ArmAsync(next.NextActionAt);
        }

        // Scheduling awaited before staging. An interleaved decision may have advanced the revision meanwhile.
        if (_effects.TryGetValue(next.ProviderKey, out var current) && current.Revision >= next.Revision)
        {
            await WriteStateAsync();
            return current;
        }

        if (outgoing is not null)
        {
            if (_effects.ContainsKey(outgoing.ProviderKey))
            {
                throw new InvalidOperationException("The on-success operation identity is already in use.");
            }
        }

        if (next.IsCompleted)
        {
            _outbox.Send(_outcomes, MessageId.Create(next.ProviderKey, "outcome"),
                GrainFactory.GetGrain<IEffectOrderGrain>(this.GetPrimaryKeyString()), next);
        }

        if (outgoing is not null)
        {
            _effects[outgoing.ProviderKey] = outgoing;
        }

        _effects[next.ProviderKey] = next;
        await WriteStateAsync();
        return next;
    }

    private async Task ArmAsync(DateTimeOffset due) =>
        _ = await _jobs.ScheduleJobAsync(new()
        {
            Target = this.GetGrainId(),
            JobName = ExternalEffectHosting.WakeupJobName,
            DueTime = due
        }, CancellationToken.None);

    private static bool CanDispatch(EffectRecord effect, DateTimeOffset now) =>
        effect.Attempts.Length < effect.Policy.MaxAttempts
        && FitsSafetyWindow(effect.Policy, now);

    private static bool FitsSafetyWindow(EffectPolicy policy, DateTimeOffset now) =>
        now + policy.AttemptTimeout < policy.Deadline
        && (policy.Execution is not EffectExecution.RetrySafe || now + policy.AttemptTimeout < policy.IdempotencySafetyDeadline);

    private static EffectRecord Complete(EffectRecord effect, ProviderOutcome outcome, string evidence) =>
        effect with
        {
            Status = outcome switch
            {
                ProviderOutcome.Applied => EffectStatus.Succeeded,
                ProviderOutcome.Rejected => EffectStatus.Rejected,
                ProviderOutcome.ProvenNoEffect when effect.CancellationRequested => EffectStatus.Canceled,
                ProviderOutcome.ProvenNoEffect => EffectStatus.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome))
            },
            MayHaveApplied = outcome is ProviderOutcome.Applied,
            Evidence = evidence,
            Revision = effect.Revision + 1,
            NextAction = "completed",
            BusinessState = outcome switch
            {
                ProviderOutcome.Applied when effect.Request.Kind is EffectKind.PaymentAuthorization => "authorized;ready-to-fulfill",
                ProviderOutcome.Applied when effect.Request.Kind is EffectKind.PaymentRefund => "refunded",
                ProviderOutcome.Applied => "email-accepted",
                ProviderOutcome.Rejected => "business-rejected",
                _ => "confirmed-no-effect"
            }
        };

    private IRecipeEffectProviderGrain Provider(EffectRecord effect) =>
        GrainFactory.GetGrain<IRecipeEffectProviderGrain>(effect.Request.ProviderAccount);

    private string Key(EffectIdentity identity) => identity.ProviderKey(this.GetGrainId().ToString());
    private EffectRecord Required(EffectIdentity identity) => _effects[Key(identity)];

    private static void CheckRevision(EffectRecord effect, long expected)
    {
        if (effect.Revision != expected)
        {
            throw new InvalidOperationException("The effect changed; reload its operational record before deciding.");
        }
    }

    private static void ValidateIdentity(EffectIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ExecutionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.OperationId);
        ArgumentOutOfRangeException.ThrowIfNegative(identity.Occurrence);
        if (identity.OperationId.EndsWith("/on-success", StringComparison.Ordinal))
        {
            throw new ArgumentException("The /on-success operation suffix is reserved for the original request's outgoing intent.", nameof(identity));
        }

        if (identity.Incarnation == Guid.Empty)
        {
            throw new ArgumentException("An effect requires the durable run incarnation.", nameof(identity));
        }
    }

    private static void ValidateRequest(EffectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);
        if (!Enum.IsDefined(request.Kind) || (request.Kind is EffectKind.PaymentAuthorization or EffectKind.PaymentRefund && request.Amount <= 0))
        {
            throw new ArgumentException("Select an effect kind and a positive payment amount.", nameof(request));
        }

        if (request.OnSuccess is { } next)
        {
            if (next.OnSuccess is not null)
            {
                throw new ArgumentException("This recipe supports one next outgoing intent.", nameof(request));
            }

            ValidateRequest(next);
        }
    }

    private static bool Supported(EffectPolicy policy, EffectKind kind) =>
        policy.Version == 1 && Enum.IsDefined(kind) && Enum.IsDefined(policy.Execution) && policy.MaxAttempts > 0
        && policy.Backoff > TimeSpan.Zero && policy.AttemptTimeout > TimeSpan.Zero && policy.RecheckInterval > TimeSpan.Zero
        && policy.AttemptTimeout.TotalMilliseconds <= uint.MaxValue - 1
        && (kind is not EffectKind.Email || policy.Execution is EffectExecution.ReconcileOnInterruption);

    private static void ValidatePolicy(EffectPolicy policy, EffectKind kind, DateTimeOffset now)
    {
        if (!Supported(policy, kind) || now + policy.AttemptTimeout >= policy.Deadline
            || (policy.Execution is EffectExecution.RetrySafe && now + policy.AttemptTimeout >= policy.IdempotencySafetyDeadline))
        {
            throw new ArgumentException("Unsupported or expired effect policy.", nameof(policy));
        }
    }
}

public sealed class EffectOrderGrain : DurableGrain, IEffectOrderGrain
{
    private readonly IDurableDictionary<string, EffectRecord> _outcomes;

    public EffectOrderGrain(IInbox inbox, Serializer serializer)
    {
        _outcomes = StateManager.GetOrAddDictionary<string, EffectRecord>("effect-outcomes");
        inbox.Register(new MessageType<EffectRecord>("external-effect-outcome", serializer), (effect, context) =>
        {
            _outcomes[effect.ProviderKey] = effect;
            context.Complete();
        });
    }

    public async Task<Dictionary<string, EffectRecord>> GetOutcomesAsync()
    {
        var result = new Dictionary<string, EffectRecord>(_outcomes);
        await WriteStateAsync();
        return result;
    }
}

public sealed class ExternalEffectWorkflowGrain : Grain, IExternalEffectWorkflowGrain
{
    public async DurableTask<EffectRecord> RunAsync(EffectRequest request)
    {
        var identity = EffectIdentity.Current("external-effect");
        var owner = GrainFactory.GetGrain<IExternalEffectGrain>(this.GetPrimaryKeyString());
        await DurableTask.Run(() => owner.SubmitAsync(identity, request)).WithId("intent");
        for (var poll = 0; ; poll++)
        {
            var result = await DurableTask.Run(async () => (await owner.GetAsync(identity))!).WithId($"read-{poll}");
            if (result.IsCompleted)
            {
                return result;
            }

            await DurableTask.Delay(TimeSpan.FromSeconds(1)).WithId($"wait-{poll}");
        }
    }
}
