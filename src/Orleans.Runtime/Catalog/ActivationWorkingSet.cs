using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime;

/// <summary>
/// Maintains a list of activations which are recently active.
/// </summary>
internal sealed partial class ActivationWorkingSet : IActivationWorkingSet, ILifecycleParticipant<ISiloLifecycle>
{
    private const byte IsIdleMask = 0b0000_0001;
    private readonly ConcurrentDictionary<IActivationWorkingSetMember, byte> _members = new();
    private readonly ILogger _logger;
    private readonly IAsyncTimer _scanPeriodTimer;
    private readonly List<IActivationWorkingSetObserver> _observers;

    private int _activeCount;
    private Task? _runTask;

    public ActivationWorkingSet(
        IAsyncTimerFactory asyncTimerFactory,
        ILogger<ActivationWorkingSet> logger,
        IEnumerable<IActivationWorkingSetObserver> observers,
        CatalogInstruments catalogInstruments,
        [FromKeyedServices(TimeProviderNames.SystemTimers)] TimeProvider timeProvider)
    {
        _logger = logger;
        _scanPeriodTimer = asyncTimerFactory.Create(TimeSpan.FromMilliseconds(5_000), nameof(ActivationWorkingSet) + "." + nameof(MonitorWorkingSet), timeProvider);
        _observers = observers.ToList();
        catalogInstruments.RegisterActivationWorkingSetObserve(() => Count);
    }

    public int Count => _activeCount;

    internal IEnumerable<IActivationWorkingSetMember> Members => EnumerateActiveMembers();

    private IEnumerable<IActivationWorkingSetMember> EnumerateActiveMembers()
    {
        foreach (var pair in _members)
        {
            if (pair.Key is IActivationWorkingSetMemberStatus status
                ? status.IsInWorkingSet && !status.IsIdle
                : (pair.Value & IsIdleMask) == 0)
            {
                yield return pair.Key;
            }
        }
    }

    public void OnActivated(IActivationWorkingSetMember member)
    {
        Debug.Assert(member is not ActivationData activation || activation.IsValid);
        if (member is ActivationData)
        {
            AddMember();
        }
        else
        {
            lock (member)
            {
                AddMember();
            }
        }

        foreach (var observer in _observers)
        {
            observer.OnAdded(member);
        }

        void AddMember()
        {
            if (!_members.TryAdd(member, 0))
            {
                throw new InvalidOperationException($"Member {member} is already a member of the working set");
            }

            if (member is IActivationWorkingSetMemberStatus status)
            {
                status.IsInWorkingSet = true;
                status.IsIdle = false;
            }

            Interlocked.Increment(ref _activeCount);
        }
    }

    public void OnActive(IActivationWorkingSetMember member)
    {
        if (member is ActivationData)
        {
            MarkActive();
        }
        else
        {
            lock (member)
            {
                MarkActive();
            }
        }

        foreach (var observer in _observers)
        {
            observer.OnActive(member);
        }

        void MarkActive()
        {
            var added = _members.TryAdd(member, 0);
            if (member is IActivationWorkingSetMemberStatus status)
            {
                status.IsInWorkingSet = true;
                status.IsIdle = false;
            }
            else if (!added)
            {
                _members.TryUpdate(member, 0, IsIdleMask);
            }

            if (added)
            {
                Interlocked.Increment(ref _activeCount);
            }
        }
    }

    public void OnEvicted(IActivationWorkingSetMember member)
    {
        bool removed;
        if (member is ActivationData)
        {
            removed = RemoveMember();
        }
        else
        {
            lock (member)
            {
                removed = RemoveMember();
            }
        }

        if (removed)
        {
            OnEvictedCore(member);
        }

        bool RemoveMember()
        {
            var result = _members.TryRemove(member, out _);
            if (result && member is IActivationWorkingSetMemberStatus status)
            {
                status.IsInWorkingSet = false;
                status.IsIdle = false;
            }

            return result;
        }
    }

    private void OnEvictedCore(IActivationWorkingSetMember member)
    {
        Interlocked.Decrement(ref _activeCount);
        foreach (var observer in _observers)
        {
            observer.OnEvicted(member);
        }
    }

    public void OnDeactivating(IActivationWorkingSetMember member)
    {
        OnEvicted(member);
        foreach (var observer in _observers)
        {
            observer.OnDeactivating(member);
        }
    }

    public void OnDeactivated(IActivationWorkingSetMember member)
    {
        OnEvicted(member);
        foreach (var observer in _observers)
        {
            observer.OnDeactivated(member);
        }
    }

    private async Task MonitorWorkingSet()
    {
        while (await _scanPeriodTimer.NextTick())
        {
            foreach (var pair in _members)
            {
                try
                {
                    VisitMember(pair.Key);
                }
                catch (Exception exception)
                {
                    LogExceptionVisitingWorkingSetMember(exception, pair.Key);
                }
            }
        }
    }

    private void VisitMember(IActivationWorkingSetMember member)
    {
        var result = MemberVisitResult.None;
        if (member is ActivationData)
        {
            VisitCore();
        }
        else
        {
            lock (member)
            {
                VisitCore();
            }
        }

        foreach (var observer in _observers)
        {
            switch (result)
            {
                case MemberVisitResult.Active:
                    observer.OnActive(member);
                    break;
                case MemberVisitResult.Idle:
                    observer.OnIdle(member);
                    break;
                case MemberVisitResult.Evicted:
                    observer.OnEvicted(member);
                    break;
            }
        }

        void VisitCore()
        {
            // Enumeration can retain a member across removal and re-addition. CLOCK state is advisory, so visit the
            // member's current state instead of adding a dictionary validation to every scan.
            var status = member as IActivationWorkingSetMemberStatus;
            byte dictionaryState = 0;
            if ((status is null && !_members.TryGetValue(member, out dictionaryState))
                || (status is not null && !status.IsInWorkingSet))
            {
                result = MemberVisitResult.None;
            }
            else
            {
                var wouldRemove = status is not null
                    ? status.IsIdle
                    : (dictionaryState & IsIdleMask) != 0;
                if (member.IsCandidateForRemoval(wouldRemove))
                {
                    if (wouldRemove)
                    {
                        if (_members.TryRemove(member, out _))
                        {
                            if (status is not null)
                            {
                                status.WasRemovedByCollection = true;
                                status.IsInWorkingSet = false;
                                status.IsIdle = false;
                            }

                            Interlocked.Decrement(ref _activeCount);
                            result = MemberVisitResult.Evicted;
                        }
                        else
                        {
                            result = MemberVisitResult.None;
                        }
                    }
                    else
                    {
                        if (status is not null)
                        {
                            status.IsIdle = true;
                        }
                        else
                        {
                            result = _members.TryUpdate(member, IsIdleMask, 0)
                                ? MemberVisitResult.Idle
                                : MemberVisitResult.None;
                        }

                        if (status is not null)
                        {
                            result = MemberVisitResult.Idle;
                        }
                    }
                }
                else
                {
                    if (wouldRemove && status is not null)
                    {
                        status.IsIdle = false;
                        result = MemberVisitResult.Active;
                    }
                    else if (wouldRemove)
                    {
                        result = _members.TryUpdate(member, 0, IsIdleMask)
                            ? MemberVisitResult.Active
                            : MemberVisitResult.None;
                    }
                    else
                    {
                        result = MemberVisitResult.Active;
                    }
                }
            }
        }
    }

    private enum MemberVisitResult
    {
        None,
        Active,
        Idle,
        Evicted
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(ActivationWorkingSet),
            ServiceLifecycleStage.BecomeActive,
            StartMonitoring,
            StopMonitoring);

        Task StartMonitoring(CancellationToken ct)
        {
            using var _ = new ExecutionContextSuppressor();
            _runTask = Task.Run(MonitorWorkingSet, CancellationToken.None);
            return Task.CompletedTask;
        }

        async Task StopMonitoring(CancellationToken ct)
        {
            _scanPeriodTimer.Dispose();
            if (_runTask is Task task)
            {
                await task.WaitAsync(ct).SuppressThrowing();
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Exception visiting working set member {Member}"
    )]
    private partial void LogExceptionVisitingWorkingSetMember(Exception exception, IActivationWorkingSetMember member);
}

/// <summary>
/// Manages the set of recently active <see cref="IGrainContext"/> instances.
/// </summary>
public interface IActivationWorkingSet
{
    /// <summary>
    /// Returns the number of grain activations which were recently active.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Adds a new member to the working set.
    /// </summary>
    void OnActivated(IActivationWorkingSetMember member);

    /// <summary>
    /// Signals that a member is active and should be in the working set.
    /// </summary>
    void OnActive(IActivationWorkingSetMember member);

    /// <summary>
    /// Signals that a member has begun to deactivate.
    /// </summary>
    /// <param name="member"></param>
    void OnDeactivating(IActivationWorkingSetMember member);

    /// <summary>
    /// Signals that a members has deactivated.
    /// </summary>
    void OnDeactivated(IActivationWorkingSetMember member);
}

/// <summary>
/// Represents an activation from the perspective of <see cref="IActivationWorkingSet"/>.
/// </summary>
public interface IActivationWorkingSetMember
{
    /// <summary>
    /// Returns <see langword="true"/> if the member is eligible for removal, <see langword="false"/> otherwise.
    /// </summary>
    /// <returns><see langword="true"/> if the member is eligible for removal, <see langword="false"/> otherwise.</returns>
    /// <remarks>
    /// If this method returns <see langword="true"/> and <paramref name="wouldRemove"/> is <see langword="true"/>, the member must be removed from the working set and is eligible to be added again via a call to <see cref="IActivationWorkingSet.OnActivated(IActivationWorkingSetMember)"/>.
    /// </remarks>
    bool IsCandidateForRemoval(bool wouldRemove);
}

internal interface IActivationWorkingSetMemberStatus : IActivationWorkingSetMember
{
    bool IsInWorkingSet { get; set; }

    bool IsIdle { get; set; }

    bool WasRemovedByCollection { get; set; }
}

/// <summary>
/// An <see cref="IActivationWorkingSet"/> observer.
/// </summary>
public interface IActivationWorkingSetObserver
{
    /// <summary>
    /// Called when an activation is added to the working set.
    /// </summary>
    void OnAdded(IActivationWorkingSetMember member) { }

    /// <summary>
    /// Called when an activation becomes active.
    /// </summary>
    void OnActive(IActivationWorkingSetMember member) { }

    /// <summary>
    /// Called when an activation becomes idle.
    /// </summary>
    void OnIdle(IActivationWorkingSetMember member) { }

    /// <summary>
    /// Called when an activation is removed from the working set.
    /// </summary>
    void OnEvicted(IActivationWorkingSetMember member) { }

    /// <summary>
    /// Called when an activation starts deactivating.
    /// </summary>
    void OnDeactivating(IActivationWorkingSetMember member) { }

    /// <summary>
    /// Called when an activation is deactivated.
    /// </summary>
    void OnDeactivated(IActivationWorkingSetMember member) { }
}
