using System;
using Orleans.Journaling.Configuration;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Unit tests for <see cref="DurableInboxOptions"/>.
/// </summary>
[TestCategory("BVT"), TestCategory("Journaling")]
public class DurableInboxOptionsTests
{
    [Fact]
    public void Constructor_WithDefaultValues_SetsExpectedDefaults()
    {
        // Act
        var options = new DurableInboxOptions();

        // Assert
        Assert.Equal(1000, options.MaxCapacity);
        Assert.Equal(TimeSpan.FromDays(7), options.DeduplicationWindow);
        Assert.Equal(1, options.ProcessingConcurrency);
        Assert.True(options.EnableLongPolling);
        Assert.Equal(TimeSpan.FromSeconds(30), options.DefaultPollTimeout);
    }

    [Fact]
    public void MaxCapacity_CanBeSet()
    {
        // Arrange
        var options = new DurableInboxOptions();

        // Act
        options.MaxCapacity = 5000;

        // Assert
        Assert.Equal(5000, options.MaxCapacity);
    }

    [Fact]
    public void DeduplicationWindow_CanBeSet()
    {
        // Arrange
        var options = new DurableInboxOptions();
        var window = TimeSpan.FromDays(30);

        // Act
        options.DeduplicationWindow = window;

        // Assert
        Assert.Equal(window, options.DeduplicationWindow);
    }

    [Fact]
    public void ProcessingConcurrency_CanBeSet()
    {
        // Arrange
        var options = new DurableInboxOptions();

        // Act
        options.ProcessingConcurrency = 8;

        // Assert
        Assert.Equal(8, options.ProcessingConcurrency);
    }

    [Fact]
    public void EnableLongPolling_CanBeSet()
    {
        // Arrange
        var options = new DurableInboxOptions();

        // Act
        options.EnableLongPolling = false;

        // Assert
        Assert.False(options.EnableLongPolling);
    }

    [Fact]
    public void DefaultPollTimeout_CanBeSet()
    {
        // Arrange
        var options = new DurableInboxOptions();
        var timeout = TimeSpan.FromSeconds(60);

        // Act
        options.DefaultPollTimeout = timeout;

        // Assert
        Assert.Equal(timeout, options.DefaultPollTimeout);
    }

    [Fact]
    public void Validate_WithDefaultValues_DoesNotThrow()
    {
        // Arrange
        var options = new DurableInboxOptions();

        // Act & Assert - should not throw
        options.Validate();
    }

    [Fact]
    public void Validate_WithValidCustomValues_DoesNotThrow()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            MaxCapacity = 5000,
            DeduplicationWindow = TimeSpan.FromDays(30),
            ProcessingConcurrency = 4,
            EnableLongPolling = false,
            DefaultPollTimeout = TimeSpan.FromMinutes(5)
        };

        // Act & Assert - should not throw
        options.Validate();
    }

    [Fact]
    public void Validate_WithZeroMaxCapacity_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            MaxCapacity = 0
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal("MaxCapacity", ex.ParamName);
        Assert.Contains("must be greater than zero", ex.Message);
    }

    [Fact]
    public void Validate_WithNegativeMaxCapacity_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            MaxCapacity = -100
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal("MaxCapacity", ex.ParamName);
        Assert.Contains("must be greater than zero", ex.Message);
    }

    [Fact]
    public void Validate_WithZeroDeduplicationWindow_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            DeduplicationWindow = TimeSpan.Zero
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal("DeduplicationWindow", ex.ParamName);
        Assert.Contains("must be greater than TimeSpan.Zero", ex.Message);
    }

    [Fact]
    public void Validate_WithNegativeDeduplicationWindow_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            DeduplicationWindow = TimeSpan.FromSeconds(-1)
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal("DeduplicationWindow", ex.ParamName);
        Assert.Contains("must be greater than TimeSpan.Zero", ex.Message);
    }

    [Fact]
    public void Validate_WithZeroProcessingConcurrency_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            ProcessingConcurrency = 0
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal("ProcessingConcurrency", ex.ParamName);
        Assert.Contains("must be greater than zero", ex.Message);
    }

    [Fact]
    public void Validate_WithNegativeProcessingConcurrency_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            ProcessingConcurrency = -1
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal("ProcessingConcurrency", ex.ParamName);
        Assert.Contains("must be greater than zero", ex.Message);
    }

    [Fact]
    public void Validate_WithZeroDefaultPollTimeout_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            DefaultPollTimeout = TimeSpan.Zero
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal("DefaultPollTimeout", ex.ParamName);
        Assert.Contains("must be greater than TimeSpan.Zero", ex.Message);
    }

    [Fact]
    public void Validate_WithNegativeDefaultPollTimeout_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            DefaultPollTimeout = TimeSpan.FromSeconds(-1)
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal("DefaultPollTimeout", ex.ParamName);
        Assert.Contains("must be greater than TimeSpan.Zero", ex.Message);
    }

    [Fact]
    public void Validate_WithMinimalValidValues_DoesNotThrow()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            MaxCapacity = 1,
            DeduplicationWindow = TimeSpan.FromTicks(1),
            ProcessingConcurrency = 1,
            DefaultPollTimeout = TimeSpan.FromTicks(1)
        };

        // Act & Assert - should not throw
        options.Validate();
    }

    [Fact]
    public void Validate_WithLargeValues_DoesNotThrow()
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            MaxCapacity = int.MaxValue,
            DeduplicationWindow = TimeSpan.FromDays(365),
            ProcessingConcurrency = 1000,
            DefaultPollTimeout = TimeSpan.FromHours(24)
        };

        // Act & Assert - should not throw
        options.Validate();
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(1000, 7)]
    [InlineData(10000, 30)]
    public void MaxCapacity_WithVariousValues_WorksCorrectly(int capacity, int deduplicationDays)
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            MaxCapacity = capacity,
            DeduplicationWindow = TimeSpan.FromDays(deduplicationDays)
        };

        // Act
        options.Validate();

        // Assert
        Assert.Equal(capacity, options.MaxCapacity);
        Assert.Equal(TimeSpan.FromDays(deduplicationDays), options.DeduplicationWindow);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    public void ProcessingConcurrency_WithVariousValues_WorksCorrectly(int concurrency, bool enableLongPolling)
    {
        // Arrange
        var options = new DurableInboxOptions
        {
            ProcessingConcurrency = concurrency,
            EnableLongPolling = enableLongPolling
        };

        // Act
        options.Validate();

        // Assert
        Assert.Equal(concurrency, options.ProcessingConcurrency);
        Assert.Equal(enableLongPolling, options.EnableLongPolling);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(300)]
    public void DefaultPollTimeout_WithVariousSeconds_WorksCorrectly(int seconds)
    {
        // Arrange
        var timeout = TimeSpan.FromSeconds(seconds);
        var options = new DurableInboxOptions
        {
            DefaultPollTimeout = timeout
        };

        // Act
        options.Validate();

        // Assert
        Assert.Equal(timeout, options.DefaultPollTimeout);
    }
}
