using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents an ownership-counted pooled message.
    /// </summary>
    /// <remarks>
    /// Each checkout carries the backing state's generation. Every access validates that generation, so a handle
    /// from an earlier checkout cannot observe, mutate, acquire, or release a later checkout. A pooled message begins
    /// with one owner. Each additional concurrent owner calls <see cref="Acquire"/> and every owner calls
    /// <see cref="Release"/> exactly once. The final release resets the backing state before returning it to the pool.
    /// </remarks>
    [Id(101)]
    internal readonly struct Message : ISpanFormattable, IEquatable<Message>
    {
        public const int LENGTH_HEADER_SIZE = 8;
        public const int LENGTH_META_HEADER = 4;
        internal const int MaxCacheInvalidationHeaderEntries = 16;

        private readonly MessageState? _state;
        private readonly ulong _generation;

        public Message()
        {
            _state = new MessageState(isPoolable: false);
            if (!_state.TryActivate(out _generation))
            {
                throw new InvalidOperationException("A newly-created message state could not be activated.");
            }
        }

        internal Message(MessageState state, ulong generation)
        {
            _state = state;
            _generation = generation;
        }

        public object? BodyObject
        {
            get => Read(static state => state.BodyObject);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.BodyObject = value;
            }
        }

        public PackedHeaders Headers
        {
            get => Read(static state => state.Headers);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers = value;
            }
        }

        [GenerateSerializer]
        public enum Directions : byte
        {
            None,
            Request,
            Response,
            OneWay
        }

        [GenerateSerializer]
        public enum ResponseTypes : byte
        {
            None,
            Success,
            Error,
            Rejection,
            Status
        }

        [GenerateSerializer]
        public enum RejectionTypes : byte
        {
            None,
            Transient,
            Overloaded,
            Unrecoverable,
            GatewayTooBusy,
            CacheInvalidation
        }

        public Directions Direction
        {
            get => Read(static state => state.Headers.Direction);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.Direction = value;
            }
        }

        public bool HasDirection => Read(static state => state.Headers.Direction) != Directions.None;

        public bool IsSenderFullyAddressed => SendingSilo is not null && !SendingGrain.IsDefault;
        public bool IsTargetFullyAddressed => TargetSilo is not null && !TargetGrain.IsDefault;

        public bool IsExpired => Read(static state => state.TimeToExpiry) is { IsDefault: false, ElapsedMilliseconds: > 0 };

        public short RetryCount
        {
            get => Read(static state => state.RetryCount);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.RetryCount = value;
            }
        }

        public bool HasCacheInvalidationHeader => CacheInvalidationHeader is { Count: > 0 };

        public bool IsSystemMessage
        {
            get => Read(static state => state.Headers.HasFlag(MessageFlags.SystemMessage));
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.SetFlag(MessageFlags.SystemMessage, value);
            }
        }

        /// <summary>
        /// Indicates whether the message does not mutate application state and therefore whether it can be interleaved with other read-only messages.
        /// </summary>
        /// <remarks>
        /// Defaults to <see langword="false"/>.
        /// </remarks>
        public bool IsReadOnly
        {
            get => Read(static state => state.Headers.HasFlag(MessageFlags.ReadOnly));
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.SetFlag(MessageFlags.ReadOnly, value);
            }
        }

        public bool IsAlwaysInterleave
        {
            get => Read(static state => state.Headers.HasFlag(MessageFlags.AlwaysInterleave));
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.SetFlag(MessageFlags.AlwaysInterleave, value);
            }
        }

        public bool IsUnordered
        {
            get => Read(static state => state.Headers.HasFlag(MessageFlags.Unordered));
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.SetFlag(MessageFlags.Unordered, value);
            }
        }

        /// <summary>
        /// Whether the message is allowed to be sent to another activation of the target grain.
        /// </summary>
        /// <remarks>
        /// Defaults to <see langword="false"/>.
        /// </remarks>
        public bool IsLocalOnly
        {
            get => Read(static state => state.Headers.HasFlag(MessageFlags.IsLocalOnly));
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.SetFlag(MessageFlags.IsLocalOnly, value);
            }
        }

        /// <summary>
        /// Whether the message is allowed to activate a grain and/or extend its lifetime.
        /// </summary>
        /// <remarks>
        /// Defaults to <see langword="true"/>.
        /// </remarks>
        public bool IsKeepAlive
        {
            get => !Read(static state => state.Headers.HasFlag(MessageFlags.SuppressKeepAlive));
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.SetFlag(MessageFlags.SuppressKeepAlive, !value);
            }
        }

        public CorrelationId Id
        {
            get => Read(static state => state.Id);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Id = value;
            }
        }

        public int ForwardCount
        {
            get => Read(static state => state.Headers.ForwardCount);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.ForwardCount = value;
            }
        }

        public SiloAddress? TargetSilo
        {
            get => Read(static state => state.TargetSilo);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.TargetSilo = value;
            }
        }

        public GrainId TargetGrain
        {
            get => Read(static state => state.TargetGrain);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.TargetGrain = value;
            }
        }

        public SiloAddress? SendingSilo
        {
            get => Read(static state => state.SendingSilo);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.SendingSilo = value;
            }
        }

        public GrainId SendingGrain
        {
            get => Read(static state => state.SendingGrain);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.SendingGrain = value;
            }
        }

        public ushort InterfaceVersion
        {
            get => Read(static state => state.InterfaceVersion);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.InterfaceVersion = value;
                mutation.State.Headers.SetFlag(MessageFlags.HasInterfaceVersion, value is not 0);
            }
        }

        public ResponseTypes Result
        {
            get => Read(static state => state.Headers.ResponseType);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.Headers.ResponseType = value;
            }
        }

        public TimeSpan? TimeToLive
        {
            get
            {
                var stopwatch = Read(static state => state.TimeToExpiry);
                return stopwatch.IsDefault ? null : -stopwatch.Elapsed;
            }
            set
            {
                if (value.HasValue)
                {
                    SetTimeToLiveMilliseconds((long)value.Value.TotalMilliseconds);
                }
                else
                {
                    SetInfiniteTimeToLive();
                }
            }
        }

        internal long GetTimeToLiveMilliseconds() => -Read(static state => state.TimeToExpiry).ElapsedMilliseconds;

        internal void SetTimeToLiveMilliseconds(long milliseconds)
        {
            using var mutation = EnterMutation();
            mutation.State.Headers.SetFlag(MessageFlags.HasTimeToLive, true);
            mutation.State.TimeToExpiry = CoarseStopwatch.StartNew(-milliseconds);
        }

        internal void SetInfiniteTimeToLive()
        {
            using var mutation = EnterMutation();
            mutation.State.Headers.SetFlag(MessageFlags.HasTimeToLive, false);
            mutation.State.TimeToExpiry = default;
        }

        public List<GrainAddressCacheUpdate>? CacheInvalidationHeader
        {
            get => Read(static state => state.CacheInvalidationHeader);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.CacheInvalidationHeader = value;
                mutation.State.Headers.SetFlag(MessageFlags.HasCacheInvalidationHeader, value is not null);
            }
        }

        public Dictionary<string, object>? RequestContextData
        {
            get => Read(static state => state.RequestContextData);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.RequestContextData = value;
                mutation.State.Headers.SetFlag(MessageFlags.HasRequestContextData, value is not null);
            }
        }

        public GrainInterfaceType InterfaceType
        {
            get => Read(static state => state.InterfaceType);
            set
            {
                using var mutation = EnterMutation();
                mutation.State.InterfaceType = value;
                mutation.State.Headers.SetFlag(MessageFlags.HasInterfaceType, !value.IsDefault);
            }
        }

        public bool IsExpirableMessage()
        {
            GrainId id = TargetGrain;
            if (id.IsDefault) return false;

            // don't set expiration for one way, system target and system grain messages.
            return Direction != Directions.OneWay && !id.IsSystemTarget();
        }

        internal void AddToCacheInvalidationHeader(GrainAddress invalidAddress, GrainAddress? validAddress)
        {
            using var mutation = EnterMutation();
            var state = mutation.State;
            var grainAddressCacheUpdate = new GrainAddressCacheUpdate(invalidAddress, validAddress);
            var cacheInvalidationHeader = state.CacheInvalidationHeader;
            if (cacheInvalidationHeader is null)
            {
                var newList = new List<GrainAddressCacheUpdate> { grainAddressCacheUpdate };
                if (Interlocked.CompareExchange(ref state.CacheInvalidationHeader, newList, null) is { } existingCacheInvalidationHeader)
                {
                    // Another thread initialized it, add to the existing list
                    lock (existingCacheInvalidationHeader)
                    {
                        AddCacheInvalidationHeaderEntry(existingCacheInvalidationHeader, grainAddressCacheUpdate);
                    }
                }
                else
                {
                    state.Headers.SetFlag(MessageFlags.HasCacheInvalidationHeader, true);
                }
            }
            else
            {
                lock (cacheInvalidationHeader)
                {
                    AddCacheInvalidationHeaderEntry(cacheInvalidationHeader, grainAddressCacheUpdate);
                }
            }
        }

        private static void AddCacheInvalidationHeaderEntry(List<GrainAddressCacheUpdate> cacheInvalidationHeader, GrainAddressCacheUpdate grainAddressCacheUpdate)
        {
            if (cacheInvalidationHeader.Count >= MaxCacheInvalidationHeaderEntries || ContainsCacheInvalidationHeaderEntry(cacheInvalidationHeader, grainAddressCacheUpdate.GrainId))
            {
                return;
            }

            cacheInvalidationHeader.Add(grainAddressCacheUpdate);
        }

        private static bool ContainsCacheInvalidationHeaderEntry(List<GrainAddressCacheUpdate> cacheInvalidationHeader, GrainId grainId)
        {
            foreach (var entry in cacheInvalidationHeader)
            {
                if (entry.GrainId.Equals(grainId))
                {
                    return true;
                }
            }

            return false;
        }

        internal void InitializeRefCount()
        {
            GetStateReference().EnsureInitialized(_generation);
        }

        internal void Acquire()
        {
            if (!GetStateReference().TryAcquire(_generation))
            {
                throw new InvalidOperationException("The message handle refers to an inactive checkout.");
            }
        }

        internal bool TryAcquire() => _state?.TryAcquire(_generation) == true;

        internal void Release()
        {
            var state = GetStateReference();
            if (state.Release(_generation))
            {
                MessagePool.ReturnCore(this, state);
            }
        }

        [Conditional("DEBUG")]
        internal void MarkTransferred(string tag)
        {
#if DEBUG
            using var mutation = EnterMutation();
            mutation.State.LastTransferTag = tag;
#endif
        }

        /// <summary>
        /// Releases this message after it has been dropped (expired, rejected, blocked, etc).
        /// Marks the transfer for debugging and releases the reference.
        /// </summary>
        /// <param name="reason">A short description of why the message was dropped.</param>
        internal void ReleaseDropped(string reason)
        {
            MarkTransferred($"Dropped:{reason}");
            Release();
        }

        /// <summary>
        /// Asserts that this message has not been released (refcount > 0).
        /// Only executes in DEBUG builds.
        /// </summary>
        [Conditional("DEBUG")]
        internal void AssertNotReleased([System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
        {
#if DEBUG
            GetState().AssertActive(_generation, caller);
#endif
        }

        public bool Equals(Message other) => ReferenceEquals(_state, other._state) && _generation == other._generation;

        public override bool Equals(object? obj) => obj is Message other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_state is null ? 0 : RuntimeHelpers.GetHashCode(_state), _generation);

        public static bool operator ==(Message left, Message right) => left.Equals(right);

        public static bool operator !=(Message left, Message right) => !left.Equals(right);

        public override string ToString() => $"{this}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> dst, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            ref var origin = ref MemoryMarshal.GetReference(dst);
            int len;

            if (IsReadOnly && !Append(ref dst, "ReadOnly ")) goto grow;
            if (IsAlwaysInterleave && !Append(ref dst, "IsAlwaysInterleave ")) goto grow;

            if (Direction == Directions.Response)
            {
                switch (Result)
                {
                    case ResponseTypes.Rejection when BodyObject is RejectionResponse rejection:
                        if (!dst.TryWrite($"{rejection.RejectionType} Rejection (info: {rejection.RejectionInfo}) ", out len)) goto grow;
                        dst = dst[len..];
                        break;

                    case ResponseTypes.Error:
                        if (!Append(ref dst, "Error ")) goto grow;
                        break;

                    case ResponseTypes.Status:
                        if (!Append(ref dst, "Status ")) goto grow;
                        break;
                }
            }

            if (!dst.TryWrite($"{Direction} [{SendingSilo} {SendingGrain}]->[{TargetSilo} {TargetGrain}]", out len)) goto grow;
            dst = dst[len..];

            if (BodyObject is { } request)
            {
                if (!dst.TryWrite($" {request}", out len)) goto grow;
                dst = dst[len..];
            }

            if (!dst.TryWrite($" #{Id}", out len)) goto grow;
            dst = dst[len..];

            if (ForwardCount > 0)
            {
                if (!dst.TryWrite($"[ForwardCount={ForwardCount}]", out len)) goto grow;
                dst = dst[len..];
            }

            charsWritten = (int)Unsafe.ByteOffset(ref origin, ref MemoryMarshal.GetReference(dst)) / sizeof(char);
            return true;

grow:
            charsWritten = 0;
            return false;

            static bool Append(ref Span<char> dst, ReadOnlySpan<char> value)
            {
                if (!value.TryCopyTo(dst))
                    return false;

                dst = dst[value.Length..];
                return true;
            }
        }

        internal bool IsPing() => Read(static state =>
            state.RequestContextData?.TryGetValue(RequestContext.PING_APPLICATION_HEADER, out var value) == true && value is bool isPing && isPing);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private T Read<T>(Func<MessageState, T> reader)
        {
            var state = GetStateReference();
            state.ValidateActive(_generation);
            var result = reader(state);
            state.ValidateAfterRead(_generation);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MessageState GetState()
        {
            var state = GetStateReference();
            state.ValidateActive(_generation);
            return state;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MessageState GetStateReference() =>
            _state ?? throw new InvalidOperationException("The message handle is uninitialized.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MutationScope EnterMutation() => new(GetStateReference(), _generation);

        internal object StateIdentity => GetState();
        internal ulong GenerationForTesting => _generation;

        private readonly ref struct MutationScope
        {
            public MutationScope(MessageState state, ulong generation)
            {
                State = state;
                state.EnterMutation(generation);
            }

            public MessageState State { get; }

            public void Dispose() => State.ExitMutation();
        }

        internal sealed class MessageState
        {
            private const ulong RefCountMask = ushort.MaxValue;
            private const ulong MaxGeneration = (1UL << 48) - 1;
            private readonly bool _isPoolable;
            private long _ownership;
            private int _activeMutations;

            public MessageState(bool isPoolable)
            {
                _isPoolable = isPoolable;
            }

            public short RetryCount;
            public CoarseStopwatch TimeToExpiry;
            public object? BodyObject;
            public PackedHeaders Headers;
            public CorrelationId Id;
            public Dictionary<string, object>? RequestContextData;
            public SiloAddress? TargetSilo;
            public GrainId TargetGrain;
            public SiloAddress? SendingSilo;
            public GrainId SendingGrain;
            public ushort InterfaceVersion;
            public GrainInterfaceType InterfaceType;
            public List<GrainAddressCacheUpdate>? CacheInvalidationHeader;

#if DEBUG
            public string? LastTransferTag;
#endif

            public bool IsPoolable => _isPoolable;
            internal uint RefCountForTesting => GetRefCount(Volatile.Read(ref _ownership));

            public bool TryActivate(out ulong generation)
            {
                while (true)
                {
                    var current = Volatile.Read(ref _ownership);
                    if (GetRefCount(current) != 0)
                    {
                        ThrowInvalidOwnershipOperation("Cannot activate message state which is still owned.");
                    }

                    if (Volatile.Read(ref _activeMutations) != 0)
                    {
                        var spinner = new SpinWait();
                        do
                        {
                            spinner.SpinOnce();
                        }
                        while (Volatile.Read(ref _activeMutations) != 0);

                        continue;
                    }

                    var currentGeneration = GetGeneration(current);
                    if (currentGeneration == MaxGeneration)
                    {
                        generation = 0;
                        return false;
                    }

                    generation = currentGeneration + 1;
                    var updated = Pack(generation, 1);
                    if (Interlocked.CompareExchange(ref _ownership, updated, current) == current)
                    {
                        return true;
                    }
                }
            }

            public void EnsureInitialized(ulong generation)
            {
                var current = Volatile.Read(ref _ownership);
                if (GetGeneration(current) != generation || GetRefCount(current) != 1)
                {
                    ThrowInvalidOwnershipOperation("The message already has an owner.");
                }
            }

            public bool TryAcquire(ulong generation)
            {
                while (true)
                {
                    var current = Volatile.Read(ref _ownership);
                    if (GetGeneration(current) != generation || GetRefCount(current) == 0)
                    {
                        return false;
                    }

                    var refCount = GetRefCount(current);
                    if (refCount == ushort.MaxValue)
                    {
                        ThrowInvalidOwnershipOperation("The message has reached its maximum owner count.");
                    }

                    var updated = Pack(generation, refCount + 1);
                    if (Interlocked.CompareExchange(ref _ownership, updated, current) == current)
                    {
                        return true;
                    }
                }
            }

            public bool Release(ulong generation)
            {
                while (true)
                {
                    var current = Volatile.Read(ref _ownership);
                    ValidateOwnership(current, generation);
                    var refCount = GetRefCount(current);
                    var updated = Pack(generation, refCount - 1);
                    if (Interlocked.CompareExchange(ref _ownership, updated, current) == current)
                    {
                        if (refCount == 1)
                        {
                            var spinner = new SpinWait();
                            while (Volatile.Read(ref _activeMutations) != 0)
                            {
                                spinner.SpinOnce();
                            }
                        }

                        return refCount == 1;
                    }
                }
            }

            public void EnterMutation(ulong generation)
            {
                var ownership = Volatile.Read(ref _ownership);
                ValidateOwnership(ownership, generation);
                Interlocked.Increment(ref _activeMutations);

                ownership = Volatile.Read(ref _ownership);
                if (GetGeneration(ownership) == generation && GetRefCount(ownership) > 0)
                {
                    return;
                }

                Interlocked.Decrement(ref _activeMutations);
                ThrowInvalidOwnershipOperation("The message handle refers to an inactive checkout.");
            }

            public void ExitMutation() => Interlocked.Decrement(ref _activeMutations);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ValidateActive(ulong generation)
            {
                ValidateOwnership(Volatile.Read(ref _ownership), generation);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ValidateAfterRead(ulong generation)
            {
                ValidateOwnership(Interlocked.Read(ref _ownership), generation);
            }

            [Conditional("DEBUG")]
            public void AssertActive(ulong generation, string? caller)
            {
#if DEBUG
                var ownership = Volatile.Read(ref _ownership);
                Debug.Assert(
                    GetGeneration(ownership) == generation && GetRefCount(ownership) > 0,
                    $"Message used after release. Caller: {caller}, Generation: {generation}, CurrentGeneration: {GetGeneration(ownership)}, RefCount: {GetRefCount(ownership)}, LastTransfer: {LastTransferTag}");
#endif
            }

            public void Reset()
            {
                RetryCount = 0;
                TimeToExpiry = default;
                BodyObject = null;
                Headers = default;
                Id = default;
                RequestContextData = null;
                TargetSilo = null;
                TargetGrain = default;
                SendingSilo = null;
                SendingGrain = default;
                InterfaceVersion = 0;
                InterfaceType = default;
                CacheInvalidationHeader = null;
#if DEBUG
                LastTransferTag = null;
#endif
            }

            private static void ValidateOwnership(long ownership, ulong generation)
            {
                if (GetGeneration(ownership) != generation || GetRefCount(ownership) == 0)
                {
                    ThrowInvalidOwnershipOperation("The message handle refers to an inactive checkout.");
                }
            }

            private static long Pack(ulong generation, uint refCount) => unchecked((long)(generation << 16 | refCount));

            private static ulong GetGeneration(long ownership) => (ulong)ownership >> 16;

            private static uint GetRefCount(long ownership) => (uint)((ulong)ownership & RefCountMask);

            private static void ThrowInvalidOwnershipOperation(string message) => throw new InvalidOperationException(message);
        }

        [Flags]
        internal enum MessageFlags : ushort
        {
            SystemMessage = 1 << 0,
            ReadOnly = 1 << 1,
            AlwaysInterleave = 1 << 2,
            Unordered = 1 << 3,

            HasRequestContextData = 1 << 4,
            HasInterfaceVersion = 1 << 5,
            HasInterfaceType = 1 << 6,
            HasCacheInvalidationHeader = 1 << 7,
            HasTimeToLive = 1 << 8,

            // Message cannot be forwarded to another activation.
            IsLocalOnly = 1 << 9,

            // Message must not trigger grain activation or extend an activation's lifetime.
            SuppressKeepAlive = 1 << 10,

            // The most significant bit is reserved, possibly for use to indicate more data follows.
            Reserved = 1 << 15,
        }

        internal struct PackedHeaders
        {
            private const uint DirectionMask = 0x000F_0000;
            private const int DirectionShift = 16;
            private const uint ResponseTypeMask = 0x00F0_0000;
            private const int ResponseTypeShift = 20;
            private const uint ForwardCountMask = 0xFF00_0000;
            private const int ForwardCountShift = 24;

            public static implicit operator PackedHeaders(uint fields) => new() { _fields = fields };
            public static implicit operator uint(PackedHeaders value) => value._fields;

            // 32 bits: HHHH_HHHH RRRR_DDDD FFFF_FFFF FFFF_FFFF
            // F: 16 bits for MessageFlags
            // D: 4 bits for Direction
            // R: 4 bits for ResponseType
            // H: 8 bits for ForwardCount (hop count)
            private uint _fields;

            public int ForwardCount
            {
                readonly get => (int)(_fields >> ForwardCountShift);
                set => _fields = (_fields & ~ForwardCountMask) | (uint)value << ForwardCountShift;
            }

            public Directions Direction
            {
                readonly get => (Directions)((_fields & DirectionMask) >> DirectionShift);
                set => _fields = (_fields & ~DirectionMask) | (uint)value << DirectionShift;
            }

            public ResponseTypes ResponseType
            {
                readonly get => (ResponseTypes)((_fields & ResponseTypeMask) >> ResponseTypeShift);
                set => _fields = (_fields & ~ResponseTypeMask) | (uint)value << ResponseTypeShift;
            }

            public readonly bool HasFlag(MessageFlags flag) => (_fields & (uint)flag) != 0;

            public void SetFlag(MessageFlags flag, bool value) => _fields = value switch
            {
                true => _fields | (uint)flag,
                false => _fields & ~(uint)flag,
            };
        }
    }
}
