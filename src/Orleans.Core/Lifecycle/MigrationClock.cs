using System;
using System.Diagnostics;
using Orleans.Concurrency;

namespace Orleans.Runtime;

/// <summary>
/// Carries a migration batch's age in its request headers, which are rewritten on transport retries.
/// </summary>
[GenerateSerializer, Immutable, SerializationCallbacks(typeof(MigrationClock.Callbacks))]
internal sealed class MigrationClock
{
    internal const string RequestContextKey = "__OrleansMigrationClock";
    internal static TimeProvider SystemClock { get; } = new CoarseTimeProvider();
    private static readonly Guid LocalEpoch = Guid.NewGuid();

    [NonSerialized]
    private TimeProvider _timeProvider = SystemClock;
    [NonSerialized]
    private TimeSpan _serializedElapsed;

    [Id(0)]
    public Guid Origin { get; set; }

    [Id(1)]
    public long TimestampMilliseconds { get; set; }

    [Id(2)]
    public TimeSpan Elapsed
    {
        get => TimeSpan.FromMilliseconds(_timeProvider.GetTimestamp() - TimestampMilliseconds);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _serializedElapsed = value;
        }
    }

    internal static MigrationClock Start(Guid? origin = null, TimeProvider? timeProvider = null)
    {
        timeProvider ??= SystemClock;
        Debug.Assert(timeProvider.TimestampFrequency == CoarseStopwatch.Frequency);
        return new()
        {
            Origin = origin ?? LocalEpoch,
            TimestampMilliseconds = timeProvider.GetTimestamp(),
            _timeProvider = timeProvider,
        };
    }

    internal sealed class Callbacks
    {
        private readonly Guid _localEpoch;
        private readonly TimeProvider _timeProvider;

        public Callbacks() : this(LocalEpoch, SystemClock)
        {
        }

        internal Callbacks(Guid localEpoch, TimeProvider timeProvider)
        {
            Debug.Assert(timeProvider.TimestampFrequency == CoarseStopwatch.Frequency);
            _localEpoch = localEpoch;
            _timeProvider = timeProvider;
        }

        public void OnDeserialized(MigrationClock clock)
        {
            ArgumentOutOfRangeException.ThrowIfEqual(clock.Origin, Guid.Empty);
            clock._timeProvider = _timeProvider;
            if (clock.Origin != _localEpoch)
            {
                // Other processes use the transmitted duration, never the sender's monotonic timestamp.
                clock.TimestampMilliseconds = _timeProvider.GetTimestamp() - (long)clock._serializedElapsed.TotalMilliseconds;
                clock.Origin = _localEpoch;
            }
            else
            {
                // Restoring headers after a failed write preserves the original local epoch, including the failed write's delay.
                ArgumentOutOfRangeException.ThrowIfGreaterThan(clock.TimestampMilliseconds, _timeProvider.GetTimestamp());
            }
        }
    }

    private sealed class CoarseTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => CoarseStopwatch.Frequency;
        public override long GetTimestamp() => CoarseStopwatch.GetTimestamp();
    }
}
