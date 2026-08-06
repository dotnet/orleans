using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Utilities;
using Xunit;

namespace NonSilo.Tests;

[TestCategory("BVT")]
public sealed class ObserverManagerTests
{
    [Fact]
    public async Task CollectionModified()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);

        for (var i = 0; i < 10; i++)
        {
            observerManager.Subscribe(i, i);
        }

        // Using predicate for an easier simulation of inserting item while enumerating.
        bool Predicate(int observer)
        {
            if (observer == 5)
            {
                observerManager.Subscribe(11, 1);
            }

            return true;
        }

        Task NotificationAsync(int observer) => Task.CompletedTask;

        var ex = await Record.ExceptionAsync(() => observerManager.Notify(NotificationAsync, Predicate));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SubscribeDuringNotify_ObserversAreUpdatedImmediately()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var notifiedObservers = new List<int>();
        var newlyAddedObserverNotified = false;

        // Subscribe some initial observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        async Task NotificationAsync(int observer)
        {
            notifiedObservers.Add(observer);

            // Add a new observer during notification of the first observer.
            if (observer == 0)
            {
                observerManager.Subscribe(100, 100);

                // Verify that the newly added observer is immediately visible in the collection.
                Assert.Contains(100, observerManager.Observers.Values);
            }

            // Check if we're processing the newly added observer.
            if (observer == 100)
            {
                newlyAddedObserverNotified = true;
            }

            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync);

        // Only the original observers should have been notified, not the one added during notification.
        Assert.Equal(5, notifiedObservers.Count);

        // The newly added observer was not notified.
        Assert.DoesNotContain(100, notifiedObservers);
        Assert.False(newlyAddedObserverNotified);
    }

    [Fact]
    public async Task UnsubscribeDuringNotify_ObserversAreUpdatedImmediately()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var notifiedObservers = new List<int>();
        var unsubscribedObserverNotified = false;

        // Subscribe some initial observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        async Task NotificationAsync(int observer)
        {
            notifiedObservers.Add(observer);

            // Unsubscribe an observer during notification of the first observer.
            if (observer == 0)
            {
                observerManager.Unsubscribe(3);

                // Verify that the unsubscribed observer is immediately removed from the collection.
                Assert.DoesNotContain(3, observerManager.Observers.Values);
            }

            // Check if we're processing the unsubscribed observer.
            if (observer == 3)
            {
                unsubscribedObserverNotified = true;
            }

            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync);

        // Even though we unsubscribed during iteration, the snapshot still contains the original observers.
        // All original observers were notified due to snapshot.
        Assert.Equal(5, notifiedObservers.Count);
        // Observer 3 was notified because it was in the snapshot.
        Assert.True(unsubscribedObserverNotified);
        // But it's no longer in the collection after notification completed.
        Assert.Equal(4, observerManager.Count);
    }

    [Fact]
    public void SyncNotify_SubscribeAndUnsubscribe_WorksCorrectly()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var notifiedObservers = new List<int>();

        // Subscribe some initial observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        void Notification(int observer)
        {
            notifiedObservers.Add(observer);

            // Add new observers and remove existing ones during notification.
            if (observer == 0)
            {
                observerManager.Subscribe(100, 100);
                observerManager.Unsubscribe(3);
            }
        }

        observerManager.Notify(Notification);

        // All original observers were notified.
        Assert.Equal(5, notifiedObservers.Count);
        // Observer 3 was notified because it was in the snapshot.
        Assert.Contains(3, notifiedObservers);
        // New observer wasn't in the original snapshot.
        Assert.DoesNotContain(100, notifiedObservers);
        // 5 original - 1 removed + 1 added = 5 total.
        Assert.Equal(5, observerManager.Count);
    }

    [Fact]
    public async Task ExceptionInNotificationCallback_RemovesObserver()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);

        // Subscribe some observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        async Task NotificationAsync(int observer)
        {
            // Throw exception for specific observer.
            if (observer == 2)
            {
                throw new InvalidOperationException("Test exception");
            }

            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync);

        // One observer should be removed.
        Assert.Equal(4, observerManager.Count);
        // Observer 2 should be removed due to exception.
        Assert.DoesNotContain(2, observerManager.Observers.Values);
    }

    [Fact]
    public void SyncNotify_ExceptionInNotificationCallback_RemovesObserver()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);

        // Subscribe some observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        void Notification(int observer)
        {
            // Throw exception for specific observer.
            if (observer == 2)
            {
                throw new InvalidOperationException("Test exception");
            }
        }

        observerManager.Notify(Notification);

        // One observer should be removed.
        Assert.Equal(4, observerManager.Count);
        // Observer 2 should be removed due to exception.
        Assert.DoesNotContain(2, observerManager.Observers.Values);
    }

    [Fact]
    public async Task ExpiredObservers_AreRemovedDuringNotify()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromMinutes(30), NullLogger.Instance);
        var currentTime = DateTime.UtcNow;
        var notifiedObservers = new List<int>();

        // Mock GetDateTime to control time.
        observerManager.GetDateTime = () => currentTime;

        // Subscribe some observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        // Move some observers into the past to make them expire.
        // Advance time by 1 hour.
        currentTime = DateTime.UtcNow.AddHours(1);

        // Notification function that tracks which observers were called.
        async Task NotificationAsync(int observer)
        {
            notifiedObservers.Add(observer);
            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync);

        // No observers should be notified as all are expired.
        Assert.Empty(notifiedObservers);
        // All observers should be removed due to expiration.
        Assert.Equal(0, observerManager.Count);
    }

    [Fact]
    public void ClearExpired_RemovesOnlyExpiredObservers()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromMinutes(30), NullLogger.Instance);
        var startTime = DateTime.UtcNow;

        // Mock GetDateTime to control time.
        observerManager.GetDateTime = () => startTime;

        // Subscribe some observers.
        for (var i = 0; i < 10; i++)
        {
            observerManager.Subscribe(i, i);
        }

        // Update last seen time for half of the observers.
        var halfTime = startTime.AddMinutes(25);
        observerManager.GetDateTime = () => halfTime;

        for (var i = 5; i < 10; i++)
        {
            // This refreshes the LastSeen time.
            observerManager.Subscribe(i, i);
        }

        // Advance time to expire only the first half.
        observerManager.GetDateTime = () => startTime.AddMinutes(35);

        observerManager.ClearExpired();

         // Half should be removed due to expiration.
        Assert.Equal(5, observerManager.Count);
        for (var i = 0; i < 5; i++)
        {
             // First half should be removed.
            Assert.DoesNotContain(i, observerManager.Observers.Values);
        }

        for (var i = 5; i < 10; i++)
        {
            // Second half should remain.
            Assert.Contains(i, observerManager.Observers.Values);
        }
    }

    [Fact]
    public void Clear_RemovesAllObservers()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);

        // Subscribe some observers.
        for (var i = 0; i < 10; i++)
        {
            observerManager.Subscribe(i, i);
        }

        // Verify initial count.
        Assert.Equal(10, observerManager.Count);

        observerManager.Clear();

        Assert.Equal(0, observerManager.Count);
        Assert.Empty(observerManager.Observers);
    }

    [Fact]
    public async Task CanModifyObserversConcurrently()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var scheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1);
        const int observerCount = 100;

        // Task 1: Subscribe observers.
        var task1 = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < observerCount; i++)
            {
                observerManager.Subscribe(i, i);
            }
        },
        CancellationToken.None,
        TaskCreationOptions.RunContinuationsAsynchronously,
        scheduler.ConcurrentScheduler);

        // Task 2: Also subscribe same observers (to test update scenario).
        var task2 = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < observerCount; i++)
            {
                // Different value to test updates.
                observerManager.Subscribe(i, i * 10);
            }
        },
        CancellationToken.None,
        TaskCreationOptions.RunContinuationsAsynchronously,
        scheduler.ConcurrentScheduler);

        // Wait for both tasks to complete.
        await Task.WhenAll(task1, task2);

        Assert.Equal(observerCount, observerManager.Count);

        // Now run notification on a different thread while modifying on main thread.
        var notifiedCount = 0;
        var tcs = new TaskCompletionSource();
        var startedTcs = new TaskCompletionSource();
        var notifyTask = Task.Factory.StartNew(async () =>
        {
            await observerManager.Notify(async observer =>
            {
                startedTcs.TrySetResult();
                await tcs.Task;
                Interlocked.Increment(ref notifiedCount);
            });
        },
        CancellationToken.None,
        TaskCreationOptions.RunContinuationsAsynchronously,
        scheduler.ConcurrentScheduler).Unwrap();
        await startedTcs.Task;

        // Simultaneously modify the collection.
        for (var i = observerCount; i < observerCount + 50; i++)
        {
            observerManager.Subscribe(i, i);
        }

        tcs.TrySetResult();
        await notifyTask;

        // We should have notified exactly observerCount observers
        // (those that were present when the snapshot was created).
        Assert.Equal(observerCount, notifiedCount);
        Assert.Equal(observerCount + 50, observerManager.Count);
    }

    [Fact]
    public void PredicateFiltersObserversToNotify()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var notifiedObservers = new List<int>();

        // Subscribe some observers.
        for (var i = 0; i < 10; i++)
        {
            observerManager.Subscribe(i, i);
        }

        bool EvenNumberPredicate(int observer) => observer % 2 == 0;

        void Notification(int observer)
        {
            notifiedObservers.Add(observer);
        }

        observerManager.Notify(Notification, EvenNumberPredicate);

        // Only even observers should be notified.
        Assert.Equal(5, notifiedObservers.Count);
        // All notified observers should be even.
        Assert.All(notifiedObservers, observer => Assert.True(observer % 2 == 0));
    }

    [Fact]
    public async Task AsyncPredicateFiltersObserversToNotify()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var notifiedObservers = new List<int>();

        // Subscribe some observers.
        for (var i = 0; i < 10; i++)
        {
            observerManager.Subscribe(i, i);
        }

        bool OddNumberPredicate(int observer) => observer % 2 == 1;

        async Task NotificationAsync(int observer)
        {
            notifiedObservers.Add(observer);
            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync, OddNumberPredicate);

        // Only odd observers should be notified.
        Assert.Equal(5, notifiedObservers.Count);
        // All notified observers should be odd.
        Assert.All(notifiedObservers, observer => Assert.True(observer % 2 == 1));
    }

    [Fact]
    public void EnumeratorWorksCorrectly()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);

        // Subscribe some observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        var enumeratedObservers = new List<int>();
        foreach (var observer in observerManager)
        {
            enumeratedObservers.Add(observer);
        }

        Assert.Equal(5, enumeratedObservers.Count);
        Assert.Equal(Enumerable.Range(0, 5), enumeratedObservers.OrderBy(x => x));
    }

    [Fact]
    public void SubscribingExistingObserver_UpdatesLastSeenTime()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromMinutes(30), NullLogger.Instance);
        var startTime = DateTime.UtcNow;

        // Mock GetDateTime to control time.
        observerManager.GetDateTime = () => startTime;

        // Subscribe initial observer.
        observerManager.Subscribe(1, 42);

        // Advance time to near expiration but not quite.
        var laterTime = startTime.AddMinutes(25);
        observerManager.GetDateTime = () => laterTime;

        // Update the subscription (same ID, same observer value).
        observerManager.Subscribe(1, 42);

        // Advance time to past the original subscription time + expiration.
        var expirationCheckTime = startTime.AddMinutes(35);
        observerManager.GetDateTime = () => expirationCheckTime;

        var notified = false;
        observerManager.Notify(observer => notified = true);

        // Observer should still be active due to the refresh.
        Assert.True(notified);
        Assert.Equal(1, observerManager.Count);
    }

    [Fact]
    public void SubscribingExistingObserver_UpdatesObserverValue()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);

        // Subscribe initial observer.
        observerManager.Subscribe(1, 100);

        // Update the subscription (same ID, different observer value).
        observerManager.Subscribe(1, 200);

        var observedValue = 0;
        observerManager.Notify(observer => observedValue = observer);

        // Should get the updated value.
        Assert.Equal(200, observedValue);
    }

    [Fact]
    public void SetExpirationDuration_AffectsExpiration()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var startTime = DateTime.UtcNow;

        // Mock GetDateTime to control time.
        observerManager.GetDateTime = () => startTime;

        // Subscribe some observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        // Change expiration to a shorter duration after subscribing.
        observerManager.ExpirationDuration = TimeSpan.FromMinutes(30);

        // Advance time to between the old and new expiration durations.
        // Past 30min, but before 1hr.
        var laterTime = startTime.AddMinutes(45);
        observerManager.GetDateTime = () => laterTime;

        var notifiedObservers = new List<int>();
        observerManager.Notify(observer => notifiedObservers.Add(observer));

        // No observers should be notified due to expiration.
        Assert.Empty(notifiedObservers);
        // All observers should be removed.
        Assert.Equal(0, observerManager.Count);
    }

    [Fact]
    public async Task ClearDuringNotification_WorksCorrectly()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var notifiedObservers = new ConcurrentBag<int>();

        // Subscribe some observers.
        for (var i = 0; i < 10; i++)
        {
            observerManager.Subscribe(i, i);
        }

        // Start notification.
        var notifyStartedTcs = new TaskCompletionSource();
        var notifyTask = Task.Run(() =>
        {
            observerManager.Notify(observer =>
            {
                notifyStartedTcs.TrySetResult();
                notifiedObservers.Add(observer);
            });
        });

        // Clear while notification is happening.
        await notifyStartedTcs.Task;
        observerManager.Clear();

        // Wait for notification to complete.
        await notifyTask;

        // Assert - We should still have notified all the original observers in the snapshot.
        Assert.Equal(10, notifiedObservers.Count);

        // But the collection should now be empty.
        Assert.Equal(0, observerManager.Count);
        Assert.Empty(observerManager.Observers);
    }

    [Fact]
    public void ModifyDuringEnumeration_WorksWithoutExceptions()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);

        // Subscribe some initial observers.
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        var enumeratedObservers = new List<int>();
        var newObserversAdded = new List<int>();
        var removedObserver = -1;

        // Enumerate using foreach which uses GetEnumerator under the hood.
        foreach (var observer in observerManager)
        {
            enumeratedObservers.Add(observer);

            // Add and remove observers during enumeration.
            if (observer == 2)
            {
                // Add new observers.
                for (var i = 10; i < 15; i++)
                {
                    observerManager.Subscribe(i, i);
                    newObserversAdded.Add(i);
                }

                // Remove an observer.
                observerManager.Unsubscribe(4);
                removedObserver = 4;

                // Verify immediate visibility in the collection (but not in the enumeration).
                Assert.DoesNotContain(4, observerManager.Observers.Values);
                foreach (var newObserver in newObserversAdded)
                {
                    Assert.Contains(newObserver, observerManager.Observers.Values);
                }
            }
        }

        // Original enumeration should complete with all original observers.
        Assert.Equal(5, enumeratedObservers.Count);
        // Even though we removed it during enumeration.
        Assert.Contains(removedObserver, enumeratedObservers);

        // Verify final collection state.
        // 5 original - 1 removed + 5 added = 9 total.
        Assert.Equal(9, observerManager.Count);
        Assert.DoesNotContain(removedObserver, observerManager);
        foreach (var newObserver in newObserversAdded)
        {
            Assert.Contains(newObserver, observerManager);
        }
    }
}
