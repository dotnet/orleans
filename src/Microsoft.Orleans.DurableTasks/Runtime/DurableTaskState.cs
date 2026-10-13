using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// The persisted state of one durable task.
/// </summary>
/// <remarks>
/// <para>
/// Every persisted member is a primitive, timestamp, string, <see cref="GrainId"/>, or set of grain identities, so every
/// journal format can store the state. The request and the result are polymorphic Orleans-serializable values, held as
/// opaque Orleans-serialized payloads in <see cref="RequestPayload"/> and <see cref="ResultPayload"/>. The payloads are
/// not serialized with the state: the state is rewritten on every update, so the owning storage persists each payload
/// separately, once, and attaches it to the state it exposes.
/// </para>
/// <para>
/// The owning storage binds each state to its <see cref="Serializer"/> before exposing it. <see cref="Request"/> and
/// <see cref="Result"/> decode their payload on first access and cache the decoded value, so recovery does not decode
/// every payload and later updates reuse the stored payloads.
/// </para>
/// <para>
/// JSON journals omit unset grain identities, so they recover as <see langword="default"/>.
/// </para>
/// </remarks>
[GenerateSerializer, SuppressReferenceTracking]
[Alias("Orleans.DurableTasks.Runtime.DurableTaskState")]
public class DurableTaskState : IDurableTaskState
{
    [NonSerialized]
    private Serializer? _serializer;
    [NonSerialized]
    private byte[]? _requestPayload;
    [NonSerialized]
    private byte[]? _resultPayload;
    [NonSerialized]
    private IDurableTaskRequest? _request;
    [NonSerialized]
    private DurableTaskResponse? _result;
    [NonSerialized]
    private bool _payloadsAttached;

    /// <summary>
    /// Gets or sets the Orleans-serialized <see cref="IDurableTaskRequest"/>, or <see langword="null"/> when the task has no stored request.
    /// </summary>
    /// <remarks>The owning storage persists the payload separately from the state.</remarks>
    [JsonIgnore]
    public byte[]? RequestPayload
    {
        get => _requestPayload;
        set
        {
            _requestPayload = value;
            _request = null;
        }
    }

    /// <summary>
    /// Gets or sets the Orleans-serialized terminal <see cref="DurableTaskResponse"/>, or <see langword="null"/> when the task has no stored result.
    /// </summary>
    /// <remarks>The owning storage persists the payload separately from the state.</remarks>
    [JsonIgnore]
    public byte[]? ResultPayload
    {
        get => _resultPayload;
        set
        {
            _resultPayload = value;
            _result = null;
        }
    }

    /// <inheritdoc cref="IDurableTaskState.Result"/>
    /// <remarks>The value is decoded from <see cref="ResultPayload"/> on first access.</remarks>
    /// <exception cref="InvalidOperationException">The state stores a result but is not bound to its storage.</exception>
    [JsonIgnore]
    public DurableTaskResponse? Result => _result ??= _resultPayload is { } payload
        ? GetSerializer().Deserialize<DurableTaskResponse>(payload)
        : null;

    /// <inheritdoc cref="IDurableTaskState.CompletionDestinations"/>
    [Id(6)]
    public HashSet<GrainId> CompletionDestinations { get; set; } = [];

    /// <inheritdoc cref="IDurableTaskState.Request"/>
    /// <remarks>The value is decoded from <see cref="RequestPayload"/> on first access.</remarks>
    /// <exception cref="InvalidOperationException">The state stores a request but is not bound to its storage.</exception>
    [JsonIgnore]
    public IDurableTaskRequest? Request => _request ??= _requestPayload is { } payload
        ? GetSerializer().Deserialize<IDurableTaskRequest>(payload)
        : null;

    /// <inheritdoc cref="IDurableTaskState.CompletedAt"/>
    [Id(3)]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CreatedAt"/>
    [Id(4)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CancellationRequestedAt"/>
    [Id(5)]
    public DateTimeOffset? CancellationRequestedAt { get; set; }

    [Id(7)]
    public DateTimeOffset? DueTime { get; set; }

    [Id(8)]
    public long ResumeGeneration { get; set; }

    [Id(9)]
    public string? RequestFingerprint { get; set; }

    [Id(10)]
    public DateTimeOffset? TombstonedAt { get; set; }

    [Id(11)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GrainId RemoteTarget { get; set; }

    [Id(12)]
    public string? RemoteRequestFingerprint { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CallerId"/>
    [Id(13)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GrainId CallerId { get; set; }

    [Id(14)]
    public TimeSpan? DelayDuration { get; set; }

    /// <inheritdoc cref="IDurableTaskState.RetainedSince"/>
    [Id(17)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? RetainedSince { get; set; }

    IReadOnlySet<GrainId> IDurableTaskState.CompletionDestinations => CompletionDestinations;

    /// <summary>
    /// Binds this state to the serializer which encodes and decodes its payloads.
    /// </summary>
    internal DurableTaskState Bind(Serializer serializer)
    {
        Debug.Assert(_serializer is null || ReferenceEquals(_serializer, serializer), "A durable task state is owned by one storage.");
        _serializer = serializer;
        return this;
    }

    /// <summary>Gets whether the payloads which the owning storage persists separately are attached.</summary>
    internal bool PayloadsAttached => _payloadsAttached;

    /// <summary>Attaches the payloads which the owning storage persists separately from a recovered state.</summary>
    internal void AttachPayloads(byte[]? request, byte[]? result)
    {
        Debug.Assert(!_payloadsAttached, "A state's payloads are attached once.");
        RequestPayload = request;
        ResultPayload = result;
        _payloadsAttached = true;
    }

    /// <summary>
    /// Shares the payloads of the state this instance was copied from. The payloads are immutable once stored.
    /// </summary>
    internal void CopyPayloadsFrom(DurableTaskState source)
    {
        RequestPayload = source._requestPayload;
        ResultPayload = source._resultPayload;
    }

    /// <summary>
    /// Stores <paramref name="request"/> as an Orleans-serialized payload and caches it as the decoded value.
    /// </summary>
    internal void SetRequest(IDurableTaskRequest? request)
    {
        _requestPayload = request is null ? null : GetSerializer().SerializeToArray(request);
        _request = request;
        _payloadsAttached = true;
    }

    /// <summary>
    /// Stores <paramref name="result"/> as an Orleans-serialized payload and caches it as the decoded value.
    /// </summary>
    internal void SetResult(DurableTaskResponse? result)
    {
        _resultPayload = result is null ? null : GetSerializer().SerializeToArray(result);
        _result = result;
    }

    private Serializer GetSerializer() =>
        _serializer ?? throw new InvalidOperationException("The durable task state is not bound to a durable task storage.");
}
