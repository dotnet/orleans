using System.Threading;
using Orleans.Caching;

namespace Orleans.Runtime.GrainDirectory;

internal sealed class GrainDirectoryCacheEntry
    : ConcurrentLruCache<GrainId, (GrainAddress ActivationAddress, int Version)>.LruItem,
      IDisposable
{
    private static readonly object Invalidated = new();
    private object? _messageTarget;

    public GrainDirectoryCacheEntry(GrainAddress address, int version)
        : this(address.GrainId, (address, version), timestamp: 0)
    {
    }

    public GrainDirectoryCacheEntry(
        GrainId grainId,
        (GrainAddress ActivationAddress, int Version) value,
        long timestamp)
        : base(grainId, value, timestamp)
    {
    }

    public GrainAddress Address => Value.ActivationAddress;

    public int Version => Value.Version;

    public bool IsValid => !ReferenceEquals(Volatile.Read(ref _messageTarget), Invalidated);

    public bool TryGetMessageTarget(out IGrainContext? messageTarget)
    {
        var target = Volatile.Read(ref _messageTarget);
        if (ReferenceEquals(target, Invalidated))
        {
            messageTarget = null;
            return false;
        }

        messageTarget = target as IGrainContext;
        return messageTarget is not null;
    }

    public bool TrySetMessageTarget(IGrainContext messageTarget)
    {
        ArgumentNullException.ThrowIfNull(messageTarget);
        var current = Volatile.Read(ref _messageTarget);
        if (ReferenceEquals(current, Invalidated))
        {
            return false;
        }

        return ReferenceEquals(current, messageTarget)
            || current is null && Interlocked.CompareExchange(ref _messageTarget, messageTarget, null) is null;
    }

    public void ClearMessageTarget(IGrainContext messageTarget)
    {
        ArgumentNullException.ThrowIfNull(messageTarget);
        Interlocked.CompareExchange(ref _messageTarget, null, messageTarget);
    }

    public void Invalidate() => Interlocked.Exchange(ref _messageTarget, Invalidated);

    public void Dispose() => Invalidate();

    public void ClearMessageTarget()
    {
        while (true)
        {
            var current = Volatile.Read(ref _messageTarget);
            if (current is null || ReferenceEquals(current, Invalidated))
            {
                return;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _messageTarget, null, current), current))
            {
                return;
            }
        }
    }
}
