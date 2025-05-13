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
        // Arrange
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

        // Act
        var ex = await Record.ExceptionAsync(() => observerManager.Notify(NotificationAsync, Predicate));

        // Assert
        Assert.Null(ex);
    }
}
