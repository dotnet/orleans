using System.Collections.Concurrent;
using System.Net;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("TestingHost")]
[TestCategory("Functional")]
public sealed class TestClusterTestsLifecycle
{
    [Fact]
    public async Task GetActiveSilos_ReturnsOnlyActiveHandlesInClusterOrder()
    {
        var allocator = new RecordingPortAllocator();
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(1, allocator);
        var generation = 0;
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
            Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(
                    name,
                    configuration,
                    Interlocked.Increment(ref generation)));

        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var primary = Assert.IsType<RecordingSiloHandle>(cluster.Primary);
        var firstSecondary = Assert.IsType<RecordingSiloHandle>(await cluster.StartAdditionalSiloAsync());
        var secondSecondary = Assert.IsType<RecordingSiloHandle>(await cluster.StartAdditionalSiloAsync());
        firstSecondary.SetInactive();

        var active = cluster.GetActiveSilos().ToArray();

        Assert.Equal(2, active.Length);
        Assert.Collection(
            active,
            handle => Assert.Same(primary, handle),
            handle => Assert.Same(secondSecondary, handle));
        Assert.DoesNotContain(firstSecondary, active);
        Assert.Equal([primary, firstSecondary, secondSecondary], cluster.Silos);
    }

    [Fact]
    public async Task RestartSiloAsync_Primary_ReplacesHandleAndPreservesPrimaryIdentity()
    {
        var allocator = new RecordingPortAllocator();
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(1, allocator);
        var created = new ConcurrentQueue<RecordingSiloHandle>();
        var generation = 0;
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
        {
            var handle = TestClusterLifecycleTestInfrastructure.CreateHandle(
                name,
                configuration,
                Interlocked.Increment(ref generation));
            created.Enqueue(handle);
            return Task.FromResult<SiloHandle>(handle);
        };
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var original = Assert.IsType<RecordingSiloHandle>(cluster.Primary);

        var replacement = Assert.IsType<RecordingSiloHandle>(await cluster.RestartSiloAsync(original));

        Assert.NotSame(original, replacement);
        Assert.Equal(Silo.PrimarySiloName, original.Name);
        Assert.Equal(original.Name, replacement.Name);
        Assert.Equal(original.InstanceNumber, replacement.InstanceNumber);
        Assert.False(original.IsActive);
        Assert.True(replacement.IsActive);
        Assert.Equal(1, original.GracefulStopCount);
        Assert.Equal(0, original.KillCount);
        Assert.Equal(1, original.DisposeCount);
        Assert.Same(replacement, cluster.Primary);
        Assert.Empty(cluster.SecondarySilos);
        Assert.Same(replacement, Assert.Single(cluster.Silos));
        Assert.Same(replacement, Assert.Single(cluster.GetActiveSilos()));
        Assert.Equal([original, replacement], created);
    }

    [Fact]
    public async Task RestartSiloAsync_Secondary_ReplacesHandleAndPreservesSiloName()
    {
        var allocator = new RecordingPortAllocator();
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(1, allocator);
        var generation = 0;
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
            Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(
                    name,
                    configuration,
                    Interlocked.Increment(ref generation)));
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var primary = Assert.IsType<RecordingSiloHandle>(cluster.Primary);
        var original = Assert.IsType<RecordingSiloHandle>(await cluster.StartAdditionalSiloAsync());
        var originalName = original.Name;
        var originalInstanceNumber = original.InstanceNumber;

        var replacement = Assert.IsType<RecordingSiloHandle>(await cluster.RestartSiloAsync(original));

        Assert.NotSame(original, replacement);
        Assert.Equal("Secondary_1", originalName);
        Assert.Equal(originalName, replacement.Name);
        Assert.Equal(originalInstanceNumber, replacement.InstanceNumber);
        Assert.False(original.IsActive);
        Assert.True(replacement.IsActive);
        Assert.Equal(1, original.GracefulStopCount);
        Assert.Equal(0, original.KillCount);
        Assert.Equal(1, original.DisposeCount);
        Assert.Same(primary, cluster.Primary);
        Assert.Same(replacement, Assert.Single(cluster.SecondarySilos));
        Assert.Collection(
            cluster.Silos,
            handle => Assert.Same(primary, handle),
            handle => Assert.Same(replacement, handle));
        Assert.Collection(
            cluster.GetActiveSilos(),
            handle => Assert.Same(primary, handle),
            handle => Assert.Same(replacement, handle));
    }

    [Fact]
    public async Task StopSiloAsync_ActiveHandle_StopsRemovesAndDisposesExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var allocator = new RecordingPortAllocator();
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(1, allocator);
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
            Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(
                    name,
                    configuration,
                    generation: 1,
                    blockStop: true));
        await cluster.DeployAsync(cancellationToken);
        var handle = Assert.IsType<RecordingSiloHandle>(cluster.Primary);
        var stopEntered = handle.StopEntered.Task;

        var stopping = cluster.StopSiloAsync(handle, cancellationToken);
        try
        {
            await stopEntered.WaitAsync(cancellationToken);
            Assert.False(stopping.IsCompleted);
            Assert.True(handle.IsActive);
            Assert.Same(handle, cluster.Primary);
            Assert.Same(handle, Assert.Single(cluster.GetActiveSilos()));
        }
        finally
        {
            handle.AllowStop();
        }

        await stopping;

        Assert.False(handle.IsActive);
        Assert.Null(cluster.Primary);
        Assert.Empty(cluster.Silos);
        Assert.Empty(cluster.GetActiveSilos());
        Assert.Equal(1, handle.GracefulStopCount);
        Assert.Equal(0, handle.KillCount);
        Assert.Equal(1, handle.DisposeCount);

        await cluster.DisposeAsync();
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, allocator.DisposeCount);
    }

    [Fact]
    public async Task KillSilo_ActiveHandle_KillsRemovesAndDisposesExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var allocator = new RecordingPortAllocator();
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(1, allocator);
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
            Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(
                    name,
                    configuration,
                    generation: 1,
                    blockStop: true));
        await cluster.DeployAsync(cancellationToken);
        var handle = Assert.IsType<RecordingSiloHandle>(cluster.Primary);
        var stopEntered = handle.StopEntered.Task;

        var killing = cluster.KillSiloAsync(handle, cancellationToken);
        try
        {
            await stopEntered.WaitAsync(cancellationToken);
            Assert.False(killing.IsCompleted);
            Assert.True(handle.IsActive);
            Assert.Same(handle, cluster.Primary);
        }
        finally
        {
            handle.AllowStop();
        }

        await killing;

        Assert.False(handle.IsActive);
        Assert.Null(cluster.Primary);
        Assert.Empty(cluster.Silos);
        Assert.Empty(cluster.GetActiveSilos());
        Assert.Equal(0, handle.GracefulStopCount);
        Assert.Equal(1, handle.KillCount);
        Assert.Equal(1, handle.DisposeCount);

        await cluster.DisposeAsync();
        Assert.Equal(1, handle.DisposeCount);
        Assert.Equal(1, allocator.DisposeCount);
    }

    [Fact]
    public async Task StartSiloAsync_WhenCreationFails_RethrowsOriginalExceptionAndRetainsNoHandle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var allocator = new RecordingPortAllocator();
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(1, allocator);
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
            Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(name, configuration, generation: 1));
        await cluster.DeployAsync(cancellationToken);
        var primary = Assert.IsType<RecordingSiloHandle>(cluster.Primary);
        var creationEntered = TestClusterLifecycleTestInfrastructure.CreateCompletionSource();
        var releaseCreation = TestClusterLifecycleTestInfrastructure.CreateCompletionSource();
        var expectedException = new InvalidOperationException("Controlled silo creation failure.");
        var failedPort = 0;
        var creationAttempts = 0;
        cluster.CreateSiloAsyncWithCancellation = async (_, configuration, _) =>
        {
            Interlocked.Increment(ref creationAttempts);
            failedPort = int.Parse(configuration["Orleans:Endpoints:SiloPort"]!);
            creationEntered.TrySetResult();
            await releaseCreation.Task;
            throw expectedException;
        };
        var entered = creationEntered.Task;

        var starting = cluster.StartAdditionalSiloAsync();
        try
        {
            await entered.WaitAsync(cancellationToken);
            Assert.False(starting.IsCompleted);
            Assert.Same(primary, Assert.Single(cluster.Silos));
            Assert.Same(primary, Assert.Single(cluster.GetActiveSilos()));
        }
        finally
        {
            releaseCreation.TrySetResult();
        }

        var actualException = await Assert.ThrowsAsync<InvalidOperationException>(() => starting);

        Assert.Same(expectedException, actualException);
        Assert.Equal("Controlled silo creation failure.", actualException.Message);
        Assert.Equal(1, creationAttempts);
        Assert.Same(primary, Assert.Single(cluster.Silos));
        Assert.Same(primary, Assert.Single(cluster.GetActiveSilos()));
        Assert.Equal(0, primary.DisposeCount);
        Assert.False(cluster.ContainsSilo(SiloAddress.New(IPAddress.Loopback, failedPort, 0)));

        var retryPort = 0;
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
        {
            retryPort = int.Parse(configuration["Orleans:Endpoints:SiloPort"]!);
            return Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(name, configuration, generation: 2));
        };

        var retry = Assert.IsType<RecordingSiloHandle>(await cluster.StartAdditionalSiloAsync());

        Assert.Equal(failedPort, retryPort);
        Assert.Equal("Secondary_1", retry.Name);
        Assert.True(cluster.ContainsSilo(retry.SiloAddress));
        Assert.Collection(
            cluster.Silos,
            handle => Assert.Same(primary, handle),
            handle => Assert.Same(retry, handle));
    }

    [Fact]
    public async Task StartSiloAsync_WhenCreationIsCancelled_RethrowsCallerCancellationAndRetainsNoHandle()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        var allocator = new RecordingPortAllocator();
        await using var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(1, allocator);
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
            Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(name, configuration, generation: 1));
        await cluster.DeployAsync(testCancellationToken);
        var primary = Assert.IsType<RecordingSiloHandle>(cluster.Primary);
        var creationEntered = TestClusterLifecycleTestInfrastructure.CreateCompletionSource();
        var releaseCreation = TestClusterLifecycleTestInfrastructure.CreateCompletionSource();
        var creationAttempts = 0;
        var cancelledPort = 0;
        cluster.CreateSiloAsyncWithCancellation = async (_, configuration, cancellationToken) =>
        {
            Interlocked.Increment(ref creationAttempts);
            cancelledPort = int.Parse(configuration["Orleans:Endpoints:SiloPort"]!);
            creationEntered.TrySetResult();
            await releaseCreation.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("The cancellation barrier was released without cancellation.");
        };
        using var callerCancellation = new CancellationTokenSource();
        var entered = creationEntered.Task;

        var starting = cluster.StartAdditionalSilosAsync(
            silosToStart: 1,
            startAdditionalSiloOnNewPort: false,
            callerCancellation.Token);
        try
        {
            await entered.WaitAsync(testCancellationToken);
            Assert.False(starting.IsCompleted);
            callerCancellation.Cancel();
            var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => starting.WaitAsync(testCancellationToken));

            Assert.Equal(callerCancellation.Token, cancellation.CancellationToken);
            Assert.Equal(1, creationAttempts);
            Assert.Same(primary, Assert.Single(cluster.Silos));
            Assert.Same(primary, Assert.Single(cluster.GetActiveSilos()));
            Assert.Equal(0, primary.DisposeCount);
            Assert.False(cluster.ContainsSilo(SiloAddress.New(IPAddress.Loopback, cancelledPort, 0)));
        }
        finally
        {
            releaseCreation.TrySetResult();
        }

        var retryPort = 0;
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
        {
            retryPort = int.Parse(configuration["Orleans:Endpoints:SiloPort"]!);
            return Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(name, configuration, generation: 2));
        };

        var retry = Assert.IsType<RecordingSiloHandle>(await cluster.StartAdditionalSiloAsync());

        Assert.Equal(cancelledPort, retryPort);
        Assert.Equal("Secondary_1", retry.Name);
        Assert.True(cluster.ContainsSilo(retry.SiloAddress));
        Assert.Collection(
            cluster.Silos,
            handle => Assert.Same(primary, handle),
            handle => Assert.Same(retry, handle));
    }

    [Fact]
    public async Task Dispose_WhenCalledTwice_DisposesHandlesAndAllocatorExactlyOnce()
    {
        var allocator = new RecordingPortAllocator();
        var cluster = TestClusterLifecycleTestInfrastructure.CreateCluster(1, allocator);
        var generation = 0;
        cluster.CreateSiloAsyncWithCancellation = (name, configuration, _) =>
            Task.FromResult<SiloHandle>(
                TestClusterLifecycleTestInfrastructure.CreateHandle(
                    name,
                    configuration,
                    Interlocked.Increment(ref generation)));
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var primary = Assert.IsType<RecordingSiloHandle>(cluster.Primary);
        var secondary = Assert.IsType<RecordingSiloHandle>(await cluster.StartAdditionalSiloAsync());

        cluster.Dispose();
        cluster.Dispose();
        await cluster.DisposeAsync();

        Assert.False(primary.IsActive);
        Assert.False(secondary.IsActive);
        Assert.Equal(1, primary.DisposeCount);
        Assert.Equal(1, secondary.DisposeCount);
        Assert.Equal(0, primary.GracefulStopCount);
        Assert.Equal(0, secondary.GracefulStopCount);
        Assert.Equal(0, primary.KillCount);
        Assert.Equal(0, secondary.KillCount);
        Assert.Equal(1, allocator.DisposeCount);
        Assert.Null(cluster.ClientHost);
        Assert.Empty(cluster.GetActiveSilos());
        Assert.Collection(
            cluster.Silos,
            handle => Assert.Same(primary, handle),
            handle => Assert.Same(secondary, handle));
    }
}
