using System.Threading;

namespace Orleans.Runtime.GrainDirectory;

internal sealed class GrainDirectoryCacheEntry : IDisposable
{
    private static readonly object Invalidated = new();
    private object? _messageTarget;

    public GrainDirectoryCacheEntry(GrainAddress address, int version)
    {
        Address = address;
        Version = version;
    }

    public GrainAddress Address { get; }

    public int Version { get; }

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

    public void Dispose() => Interlocked.Exchange(ref _messageTarget, Invalidated);
}
