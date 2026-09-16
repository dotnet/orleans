using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Timers;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class OutboxPumpTimerProbe
{
    private Barrier? _barrier;

    public Barrier BlockNext(int participantCount = 2)
    {
        var barrier = new Barrier(this, participantCount);
        if (Interlocked.CompareExchange(ref _barrier, barrier, null) is not null)
        {
            throw new InvalidOperationException("An outbox pump timer barrier is already armed.");
        }

        return barrier;
    }

    internal Task WaitIfBlockedAsync(object? state, CancellationToken cancellationToken)
    {
        if (state?.GetType().FullName != "Orleans.DurableMessaging.DurableOutbox+PumpTimerState")
        {
            return Task.CompletedTask;
        }

        return Volatile.Read(ref _barrier)?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    private void Remove(Barrier barrier) =>
        Interlocked.CompareExchange(ref _barrier, null, barrier);

    public sealed class Barrier : IDisposable
    {
        private readonly OutboxPumpTimerProbe _owner;
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining;

        internal Barrier(OutboxPumpTimerProbe owner, int participantCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(participantCount);
            _owner = owner;
            _remaining = participantCount;
        }

        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            var remaining = Interlocked.Decrement(ref _remaining);
            if (remaining < 0)
            {
                return;
            }

            if (remaining == 0)
            {
                _entered.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);
        }

        public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Release() => _release.TrySetResult();

        public void Dispose()
        {
            Release();
            _owner.Remove(this);
        }
    }
}

internal sealed class ControlledTimerRegistry(
    ITimerRegistry inner,
    OutboxPumpTimerProbe probe) : ITimerRegistry
{
    [Obsolete]
    public IDisposable RegisterTimer(
        IGrainContext grainContext,
        Func<object?, Task> callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) =>
        inner.RegisterTimer(grainContext, callback, state, dueTime, period);

    public IGrainTimer RegisterGrainTimer<TState>(
        IGrainContext grainContext,
        Func<TState, CancellationToken, Task> callback,
        TState state,
        GrainTimerCreationOptions options) =>
        inner.RegisterGrainTimer(
            grainContext,
            async (callbackState, cancellationToken) =>
            {
                await probe.WaitIfBlockedAsync(callbackState, cancellationToken);
                await callback(callbackState, cancellationToken);
            },
            state,
            options);

    public static void Decorate(IServiceCollection services, OutboxPumpTimerProbe probe)
    {
        var descriptor = services.Last(service => service.ServiceType == typeof(ITimerRegistry));
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(ITimerRegistry),
            serviceProvider => new ControlledTimerRegistry(CreateInner(serviceProvider, descriptor), probe),
            descriptor.Lifetime));
    }

    private static ITimerRegistry CreateInner(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ITimerRegistry instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return (ITimerRegistry)factory(serviceProvider);
        }

        return (ITimerRegistry)ActivatorUtilities.CreateInstance(
            serviceProvider,
            descriptor.ImplementationType
                ?? throw new InvalidOperationException("The timer registry registration has no implementation."));
    }
}
