using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;

namespace Orleans.Runtime
{
    /// <summary>
    /// Maintains a list of activations which are recently active.
    /// </summary>
    internal sealed class ActivationWorkingSet : IActivationWorkingSet, ILifecycleParticipant<ISiloLifecycle>
    {
        private class MemberState
        {
            public bool IsMarkedForRemoval { get; set; }
        }

        private readonly ConcurrentDictionary<IActivationWorkingSetMember, MemberState> _members = new();
        private readonly ILogger _logger;
        private readonly IAsyncTimer _scanPeriodTimer;
        private readonly List<IActivationWorkingSetObserver> _observers;

        private int _activeCount;
        private Task _runTask;

        public ActivationWorkingSet(
            IAsyncTimerFactory asyncTimerFactory,
            ILogger<ActivationWorkingSet> logger,
            IEnumerable<IActivationWorkingSetObserver> observers)
        {
            _logger = logger;
            _scanPeriodTimer = asyncTimerFactory.Create(TimeSpan.FromMilliseconds(100), nameof(ActivationWorkingSet) + "." + nameof(MonitorWorkingSet));
            _observers = observers.ToList();
        }

        public int Count => _activeCount;

        public void OnActivated(IActivationWorkingSetMember member)
        {
            if (!_members.TryAdd(member, new MemberState()))
            {
                throw new InvalidOperationException($"Member {member} is already a member of the working set");
            }

            Interlocked.Increment(ref _activeCount);
            foreach (var observer in _observers)
            {
                observer.OnAdded(member);
            }
        }

        public void OnActive(IActivationWorkingSetMember member)
        {
            if (_members.TryGetValue(member, out var state))
            {
                state.IsMarkedForRemoval = false;
            }
            else if (_members.TryAdd(member, new()))
            {
                Interlocked.Increment(ref _activeCount);
            }

            foreach (var observer in _observers)
            {
                observer.OnActive(member);
            }
        }

        public void OnEvicted(IActivationWorkingSetMember member)
        {
            if (_members.TryRemove(member, out _))
            {
                Interlocked.Decrement(ref _activeCount);
                foreach (var observer in _observers)
                {
                    observer.OnEvicted(member);
                }
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

        private void VisitMember(IActivationWorkingSetMember member, MemberState state)
        {
            var wouldRemove = state.IsMarkedForRemoval;
            if (member.IsCandidateForRemoval(wouldRemove))
            {
                if (wouldRemove)
                {
                    OnEvicted(member);
                }
                else
                {
                    state.IsMarkedForRemoval = true;
                    foreach (var observer in _observers)
                    {
                        observer.OnIdle(member);
                    }
                }
            }
            else
            {
                state.IsMarkedForRemoval = false;
                foreach (var observer in _observers)
                {
                    observer.OnActive(member);
                }
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
                        VisitMember(pair.Key, pair.Value);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Exception visiting working set member {Member}", pair.Key);
                    }
                }
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(ActivationWorkingSet),
                ServiceLifecycleStage.BecomeActive,
                ct =>
                {
                    _runTask = Task.Run(this.MonitorWorkingSet);
                    return Task.CompletedTask;
                },
                async ct =>
                {
                    _scanPeriodTimer.Dispose();
                    if (_runTask is Task task)
                    {
                        await Task.WhenAny(task, ct.WhenCancelled());
                    }
                });
        }
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
}
