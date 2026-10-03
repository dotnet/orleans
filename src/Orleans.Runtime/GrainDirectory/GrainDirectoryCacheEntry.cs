using System.Threading;
using Orleans.Caching;

namespace Orleans.Runtime.GrainDirectory;

internal sealed class GrainDirectoryCacheEntry
    : ConcurrentLruCache<GrainId, (GrainAddress ActivationAddress, int Version)>.LruItem,
      IDisposable
{
    private static readonly object Invalidated = new();
    private static readonly object Updating = new();
    private readonly LruGrainDirectoryCache? _owner;
    private readonly WeakReference<GrainDirectoryCacheEntry> _referenceHandle;
    private object? _messageTarget;

    public GrainDirectoryCacheEntry(GrainAddress address, int version)
        : this(owner: null, address.GrainId, (address, version), timestamp: 0)
    {
    }

    public GrainDirectoryCacheEntry(
        LruGrainDirectoryCache? owner,
        GrainId grainId,
        (GrainAddress ActivationAddress, int Version) value,
        long timestamp)
        : base(grainId, value, timestamp)
    {
        _owner = owner;
        _referenceHandle = new(this);
    }

    public GrainAddress Address => Value.ActivationAddress;

    public int Version => Value.Version;

    public WeakReference<GrainDirectoryCacheEntry> ReferenceHandle => _referenceHandle;

    public bool TryTouch()
    {
        if (!IsValid)
        {
            return false;
        }

        _owner?.Touch(this);
        return IsValid;
    }

    public bool IsValid
    {
        get
        {
            var target = Volatile.Read(ref _messageTarget);
            return !ReferenceEquals(target, Invalidated) && !ReferenceEquals(target, Updating);
        }
    }

    public bool TryGetMessageTarget(out object? messageTarget)
    {
        var target = Volatile.Read(ref _messageTarget);
        if (ReferenceEquals(target, Invalidated) || ReferenceEquals(target, Updating))
        {
            messageTarget = null;
            return false;
        }

        messageTarget = target;
        return messageTarget is not null;
    }

    public bool TrySetMessageTarget(object messageTarget, GrainAddress expectedAddress)
    {
        ArgumentNullException.ThrowIfNull(messageTarget);
        ArgumentNullException.ThrowIfNull(expectedAddress);
        if (!Address.Matches(expectedAddress) || !TrySetMessageTargetCore(messageTarget))
        {
            return false;
        }

        if (Address.Matches(expectedAddress))
        {
            return true;
        }

        ClearMessageTarget(messageTarget);
        return false;
    }

    public bool TrySetMessageTarget(object messageTarget, SiloAddress expectedSilo)
    {
        ArgumentNullException.ThrowIfNull(messageTarget);
        ArgumentNullException.ThrowIfNull(expectedSilo);
        if (Address.SiloAddress?.Equals(expectedSilo) != true || !TrySetMessageTargetCore(messageTarget))
        {
            return false;
        }

        if (Address.SiloAddress?.Equals(expectedSilo) == true)
        {
            return true;
        }

        ClearMessageTarget(messageTarget);
        return false;
    }

    private bool TrySetMessageTargetCore(object messageTarget)
    {
        var current = Volatile.Read(ref _messageTarget);
        if (ReferenceEquals(current, Invalidated) || ReferenceEquals(current, Updating))
        {
            return false;
        }

        return ReferenceEquals(current, messageTarget)
            || current is null && Interlocked.CompareExchange(ref _messageTarget, messageTarget, null) is null;
    }

    public void ClearMessageTarget(object messageTarget)
    {
        ArgumentNullException.ThrowIfNull(messageTarget);
        Interlocked.CompareExchange(ref _messageTarget, null, messageTarget);
    }

    public void Invalidate() => Interlocked.Exchange(ref _messageTarget, Invalidated);

    public void Dispose() => Invalidate();

    internal void Update((GrainAddress ActivationAddress, int Version) value)
    {
        var updateStarted = TryBeginUpdate();
        try
        {
            Value = value;
        }
        finally
        {
            if (updateStarted)
            {
                EndUpdate();
            }
        }
    }

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

    internal bool TryBeginUpdate()
    {
        while (true)
        {
            var current = Volatile.Read(ref _messageTarget);
            if (ReferenceEquals(current, Invalidated))
            {
                return false;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _messageTarget, Updating, current), current))
            {
                return true;
            }
        }
    }

    internal void EndUpdate() => Interlocked.CompareExchange(ref _messageTarget, null, Updating);
}
