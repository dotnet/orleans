using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orleans.Concurrency;
using Orleans.Durable.Tasks;

namespace DurableWorkflows;

[GenerateSerializer]
public sealed record EffectIdentity(
    [property: Id(0)] string ExecutionId,
    [property: Id(1)] Guid Incarnation,
    [property: Id(2)] string OperationId,
    [property: Id(3)] int Occurrence)
{
    public static EffectIdentity Current(string operationId, int occurrence = 0) =>
        new(DurableTask.Current.Id.ToString(), DurableTask.Current.Incarnation, operationId, occurrence);

    public string ProviderKey(string owner) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new[] { owner, ExecutionId, Incarnation.ToString("N"), OperationId, Occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture) }))));
}

public enum EffectKind { PaymentAuthorization, IdempotentEmail, Email, PaymentRefund }
public enum EffectExecution { RetrySafe, ReconcileOnInterruption }
public enum EffectStatus { Pending, Attempting, Reconciling, Blocked, Succeeded, Rejected, Failed, Canceled }
public enum ProviderOutcome { Applied, Rejected, ProvenNoEffect, Unknown }

[GenerateSerializer]
public sealed record EffectRequest(
    [property: Id(0)] EffectKind Kind,
    [property: Id(1)] string ProviderAccount,
    [property: Id(2)] string Destination,
    [property: Id(3)] string Content,
    [property: Id(4)] decimal Amount = 0,
    [property: Id(5)] string Currency = "USD",
    [property: Id(6)] EffectRequest? OnSuccess = null);

[GenerateSerializer]
public sealed record EffectPolicy(
    [property: Id(0)] int Version,
    [property: Id(1)] EffectExecution Execution,
    [property: Id(2)] int MaxAttempts,
    [property: Id(3)] TimeSpan Backoff,
    [property: Id(4)] TimeSpan AttemptTimeout,
    [property: Id(5)] DateTimeOffset Deadline,
    [property: Id(6)] DateTimeOffset IdempotencySafetyDeadline,
    [property: Id(7)] TimeSpan RecheckInterval);

// Changing these defaults affects newly accepted effects. Existing records carry their effective policy.
public sealed class ExternalEffectOptions
{
    public const string ClockName = "external-effects";
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan Backoff { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan ProviderKeyLifetime { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    public EffectPolicy Capture(EffectKind kind, DateTimeOffset now) =>
        new(1, kind is EffectKind.Email ? EffectExecution.ReconcileOnInterruption : EffectExecution.RetrySafe,
            MaxAttempts, Backoff, AttemptTimeout, now + Lifetime, now + ProviderKeyLifetime, RecheckInterval);
}

[GenerateSerializer]
public sealed record EffectAttempt(
    [property: Id(0)] Guid Id,
    [property: Id(1)] DateTimeOffset Started,
    [property: Id(2)] string Evidence,
    [property: Id(3)] ProviderOutcome? Outcome = null);

[GenerateSerializer]
public sealed record EffectRecord
{
    [Id(0)] public required EffectIdentity Identity { get; init; }
    [Id(1)] public required EffectRequest Request { get; init; }
    [Id(2)] public required string ProviderKey { get; init; }
    [Id(3)] public required EffectPolicy Policy { get; init; }
    [Id(4)] public EffectStatus Status { get; init; }
    [Id(5)] public long Revision { get; init; }
    [Id(6)] public EffectAttempt[] Attempts { get; init; } = [];
    [Id(7)] public bool MayHaveApplied { get; init; }
    [Id(8)] public bool CancellationRequested { get; init; }
    [Id(9)] public required DateTimeOffset NextActionAt { get; init; }
    [Id(10)] public required string NextAction { get; init; }
    [Id(11)] public required string ResolutionOwner { get; init; }
    [Id(12)] public string Evidence { get; init; } = "";
    [Id(13)] public string? RelatedEffectKey { get; init; }
    [Id(14)] public string? Authorization { get; init; }
    [Id(15)] public string BusinessState { get; init; } = "awaiting-effect";
    [Id(16)] public string? FollowUpEffectKey { get; init; }
    [Id(17)] public EffectPolicy? OnSuccessPolicy { get; init; }
    [Id(18)] public string? NextEffectKey { get; init; }
    [Id(19)] public string? CancellationReason { get; init; }

    public bool IsCompleted => Status is EffectStatus.Succeeded or EffectStatus.Rejected or EffectStatus.Failed or EffectStatus.Canceled;
}

[GenerateSerializer]
public sealed record ProviderResult([property: Id(0)] ProviderOutcome Outcome, [property: Id(1)] string Evidence);

public interface IExternalEffectGrain : IGrainWithStringKey
{
    Task<EffectRecord> SubmitAsync(EffectIdentity identity, EffectRequest request);
    Task<EffectRecord?> GetAsync(EffectIdentity identity);
    Task RunDueAsync();
    [AlwaysInterleave] Task<EffectRecord> CancelAsync(EffectIdentity identity, string reason);
    [AlwaysInterleave] Task<EffectRecord> ResolveAsync(EffectIdentity identity, long expectedRevision, ProviderOutcome outcome, string evidence);
    Task<EffectRecord> AuthorizeFollowUpAsync(EffectIdentity original, EffectIdentity followUp, string authorization, EffectRequest? request = null);
    Task<EffectRecord> ChangePolicyAsync(EffectIdentity identity, long expectedRevision, EffectPolicy policy, string authorization);
}

// The provider journal represents a separate service's authority. Replace this gateway with the provider's HTTP SDK
// while preserving its outcome classification, retention contract and lookup semantics.
public interface IRecipeEffectProviderGrain : IGrainWithStringKey
{
    Task<ProviderResult> DispatchAsync(string key, EffectRequest request, Guid attemptId, CancellationToken cancellationToken);
    Task<ProviderResult> LookupAsync(string key, EffectRequest request, CancellationToken cancellationToken);
    [AlwaysInterleave] Task<RecipeProviderSnapshot> GetSnapshotAsync();
    Task ConfigureAsync(RecipeProviderBehavior behavior);
    [AlwaysInterleave] Task HoldNextResponseAsync();
    [AlwaysInterleave] Task ReleaseResponseAsync();
}

public enum RecipeProviderBehavior { Apply, Reject, ProveNoEffect, LoseResponseAfterApply, LookupUnavailable, UnavailableBeforeApply }

[GenerateSerializer]
public sealed record RecipeProviderSnapshot(
    [property: Id(0)] Dictionary<string, EffectRequest> Applied,
    [property: Id(1)] string[] DispatchKeys,
    [property: Id(2)] Guid[] AttemptIds);

public interface IExternalEffectWorkflowGrain : IGrainWithStringKey
{
    DurableTask<EffectRecord> RunAsync(EffectRequest request);
}

public interface IEffectOrderGrain : IGrainWithStringKey
{
    Task<Dictionary<string, EffectRecord>> GetOutcomesAsync();
}
