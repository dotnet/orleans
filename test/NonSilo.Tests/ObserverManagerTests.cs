using System;
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

        bool Predicate(int observer)
        {
            // Using predicate for an easier simulation of inserting item while enumerating
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

        // Subscribe some initial observers
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        async Task NotificationAsync(int observer)
        {
            notifiedObservers.Add(observer);

            // Add a new observer during notification of the first observer
            if (observer == 0)
            {
                observerManager.Subscribe(100, 100);

                // Verify that the newly added observer is immediately visible in the collection
                Assert.Contains(100, observerManager.Observers.Values);
            }

            // Check if we're processing the newly added observer
            if (observer == 100)
            {
                newlyAddedObserverNotified = true;
            }

            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync);

        // Only the original observers should have been notified, not the one added during notification
        Assert.Equal(5, notifiedObservers.Count);

        // The newly added observer was not notified
        Assert.DoesNotContain(100, notifiedObservers);
        Assert.False(newlyAddedObserverNotified);
    }

    [Fact]
    public async Task UnsubscribeDuringNotify_ObserversAreUpdatedImmediately()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var notifiedObservers = new List<int>();
        var unsubscribedObserverNotified = false;

        // Subscribe some initial observers
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        async Task NotificationAsync(int observer)
        {
            notifiedObservers.Add(observer);
            
            // Unsubscribe an observer during notification of the first observer
            if (observer == 0)
            {
                observerManager.Unsubscribe(3);
                
                // Verify that the unsubscribed observer is immediately removed from the collection
                Assert.DoesNotContain(3, observerManager.Observers.Values);
            }
            
            // Check if we're processing the unsubscribed observer
            if (observer == 3)
            {
                unsubscribedObserverNotified = true;
            }
            
            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync);

        // Even though we unsubscribed during iteration, the snapshot still contains the original observers
        Assert.Equal(5, notifiedObservers.Count); // All original observers were notified due to snapshot
        Assert.True(unsubscribedObserverNotified); // Observer 3 was notified because it was in the snapshot
        Assert.Equal(4, observerManager.Count); // But it's no longer in the collection after notification completed
    }

    [Fact]
    public void SyncNotify_SubscribeAndUnsubscribe_WorksCorrectly()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        var notifiedObservers = new List<int>();

        // Subscribe some initial observers
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        void Notification(int observer)
        {
            notifiedObservers.Add(observer);
            
            // Add new observers and remove existing ones during notification
            if (observer == 0)
            {
                observerManager.Subscribe(100, 100);
                observerManager.Unsubscribe(3);
            }
        }

        observerManager.Notify(Notification);

        Assert.Equal(5, notifiedObservers.Count); // All original observers were notified
        Assert.Contains(3, notifiedObservers); // Observer 3 was notified because it was in the snapshot
        Assert.DoesNotContain(100, notifiedObservers); // New observer wasn't in the original snapshot
        Assert.Equal(5, observerManager.Count); // 5 original - 1 removed + 1 added = 5 total
    }

    [Fact]
    public async Task ExceptionInNotificationCallback_RemovesObserver()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        
        // Subscribe some observers
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        async Task NotificationAsync(int observer)
        {
            // Throw exception for specific observer
            if (observer == 2)
            {
                throw new InvalidOperationException("Test exception");
            }
            
            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync);

        Assert.Equal(4, observerManager.Count); // One observer should be removed
        Assert.DoesNotContain(2, observerManager.Observers.Values); // Observer 2 should be removed due to exception
    }

    [Fact]
    public void SyncNotify_ExceptionInNotificationCallback_RemovesObserver()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        
        // Subscribe some observers
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }

        void Notification(int observer)
        {
            // Throw exception for specific observer
            if (observer == 2)
            {
                throw new InvalidOperationException("Test exception");
            }
        }

        observerManager.Notify(Notification);

        Assert.Equal(4, observerManager.Count); // One observer should be removed
        Assert.DoesNotContain(2, observerManager.Observers.Values); // Observer 2 should be removed due to exception
    }

    [Fact]
    public async Task ExpiredObservers_AreRemovedDuringNotify()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromMinutes(30), NullLogger.Instance);
        var currentTime = DateTime.UtcNow;
        var notifiedObservers = new List<int>();
        
        // Mock GetDateTime to control time
        observerManager.GetDateTime = () => currentTime;
        
        // Subscribe some observers
        for (var i = 0; i < 5; i++)
        {
            observerManager.Subscribe(i, i);
        }
        
        // Move some observers into the past to make them expire
        currentTime = DateTime.UtcNow.AddHours(1); // Advance time by 1 hour
        
        // Notification function that tracks which observers were called
        async Task NotificationAsync(int observer)
        {
            notifiedObservers.Add(observer);
            await Task.CompletedTask;
        }

        await observerManager.Notify(NotificationAsync);

        Assert.Empty(notifiedObservers); // No observers should be notified as all are expired
        Assert.Equal(0, observerManager.Count); // All observers should be removed due to expiration
    }

    [Fact]
    public void ClearExpired_RemovesOnlyExpiredObservers()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromMinutes(30), NullLogger.Instance);
        var startTime = DateTime.UtcNow;
        
        // Mock GetDateTime to control time
        observerManager.GetDateTime = () => startTime;
        
        // Subscribe some observers
        for (var i = 0; i < 10; i++)
        {
            observerManager.Subscribe(i, i);
        }
        
        // Update last seen time for half of the observers
        var halfTime = startTime.AddMinutes(25);
        observerManager.GetDateTime = () => halfTime;
        
        for (var i = 5; i < 10; i++)
        {
            // This refreshes the LastSeen time
            observerManager.Subscribe(i, i);
        }
        
        // Advance time to expire only the first half
        observerManager.GetDateTime = () => startTime.AddMinutes(35);
        
        observerManager.ClearExpired();

         // Half should be removed due to expiration
        Assert.Equal(5, observerManager.Count);
        for (var i = 0; i < 5; i++)
        {
             // First half should be removed
            Assert.DoesNotContain(i, observerManager.Observers.Values);
        }

        for (var i = 5; i < 10; i++)
        {
            // Second half should remain
            Assert.Contains(i, observerManager.Observers.Values);
        }
    }

    [Fact]
    public void Clear_RemovesAllObservers()
    {
        var observerManager = new ObserverManager<int, int>(TimeSpan.FromHours(1), NullLogger.Instance);
        
        // Subscribe some observers
        for (var i = 0; i < 10; i++)
        {
            observerManager.Subscribe(i, i);
        }
        
        Assert.Equal(10, observerManager.Count); // Verify initial count
        
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
        
        // Task 1: Subscribe observers
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
        
        // Task 2: Also subscribe same observers (to test update scenario)
        var task2 = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < observerCount; i++)
            {
                observerManager.Subscribe(i, i * 10); // Different value to test updates
            }
        },
        CancellationToken.None,
        TaskCreationOptions.RunContinuationsAsynchronously,
        scheduler.ConcurrentScheduler);
        
        // Wait for both tasks to complete
        await Task.WhenAll(task1, task2);
        
        Assert.Equal(observerCount, observerManager.Count);
        
        // Now run notification on a different thread while modifying on main thread
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
        
        // Simultaneously modify the collection
        for (var i = observerCount; i < observerCount + 50; i++)
        {
            observerManager.Subscribe(i, i);
        }

        tcs.TrySetResult();
        await notifyTask;
        
        // We should have notified exactly observerCount observers
        // (those that were present when the snapshot was created)
        Assert.Equal(observerCount, notifiedCount);
        Assert.Equal(observerCount + 50, observerManager.Count);
    }
}
