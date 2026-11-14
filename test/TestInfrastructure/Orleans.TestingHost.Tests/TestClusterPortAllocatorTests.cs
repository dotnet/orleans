using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public class TestClusterPortAllocatorTests
{
    [Fact]
    public void DefaultPortRanges_AreDisjointAndBelowDynamicClientPorts()
    {
        Assert.True(TestClusterPortAllocator.GatewayPortRangeStart < TestClusterPortAllocator.GatewayPortRangeEnd);
        Assert.True(TestClusterPortAllocator.GatewayPortRangeEnd <= TestClusterPortAllocator.SiloPortRangeStart);
        Assert.True(TestClusterPortAllocator.SiloPortRangeStart < TestClusterPortAllocator.SiloPortRangeEnd);
        Assert.True(TestClusterPortAllocator.SiloPortRangeEnd < 32_768);
    }

    [Fact]
    public void AllocateConsecutivePortPairs_KeepsAllocatedBlocksWithinDefaultRanges()
    {
        const int blockSize = 7;
        using var allocator = new TestClusterPortAllocator();

        var (siloPort, gatewayPort) = allocator.AllocateConsecutivePortPairs(blockSize);

        Assert.InRange(
            siloPort,
            TestClusterPortAllocator.SiloPortRangeStart,
            TestClusterPortAllocator.SiloPortRangeEnd - blockSize);
        Assert.InRange(
            gatewayPort,
            TestClusterPortAllocator.GatewayPortRangeStart,
            TestClusterPortAllocator.GatewayPortRangeEnd - blockSize);
    }

    [Fact]
    public void AllocateConsecutivePortPairs_WhenGatewayAllocationFails_ReleasesSiloPorts()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var gatewayPort = Assert.IsType<IPEndPoint>(listener.LocalEndPoint).Port;
        var siloPort = GetAvailablePort(30_001, 32_768, gatewayPort);
        using var allocator = new TestClusterPortAllocator();
        using var verifier = new TestClusterPortAllocator();

        var exception = Assert.Throws<InvalidOperationException>(() => allocator.AllocateConsecutivePortPairs(
            [],
            numPorts: 1,
            siloPort,
            siloPort + 1,
            gatewayPort,
            gatewayPort + 1));
        Assert.Contains($"range [{gatewayPort}, {gatewayPort + 1})", exception.Message);

        Assert.Equal(
            siloPort,
            verifier.GetAvailableConsecutiveServerPorts([], siloPort, siloPort + 1, consecutivePortsToCheck: 1));
    }

    [Fact]
    public void GetAvailableConsecutiveServerPorts_WhenSocketBindFails_ReportsRejectionDiagnostics()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var port = Assert.IsType<IPEndPoint>(listener.LocalEndPoint).Port;
        using var allocator = new TestClusterPortAllocator();

        var exception = Assert.Throws<InvalidOperationException>(
            () => allocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));

        Assert.Contains($"range [{port}, {port + 1}) after 100 attempts in ", exception.Message);
        Assert.Contains("active TCP listener=0", exception.Message);
        Assert.Contains($"socket bind failure=100 (last failure: {port} ", exception.Message);
        Assert.Contains("reservation held by this process=0", exception.Message);
        Assert.Contains("reservation held by another process=0", exception.Message);
        Assert.Contains("reservation error=0", exception.Message);
    }

    [Fact]
    public void GetAvailableConsecutiveServerPorts_WhenReservedByCurrentProcess_ReportsAndReleasesReservation()
    {
        var port = GetAvailablePort(30_001, 32_768);
        using var firstAllocator = new TestClusterPortAllocator();
        using var secondAllocator = new TestClusterPortAllocator();
        using var thirdAllocator = new TestClusterPortAllocator();
        Assert.Equal(port, firstAllocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));

        var exception = Assert.Throws<InvalidOperationException>(
            () => secondAllocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));

        Assert.Contains("socket bind failure=0", exception.Message);
        Assert.Contains($"reservation held by this process=100 (last port: {port})", exception.Message);
        Assert.Contains("reservation held by another process=0", exception.Message);
        Assert.Contains("reservation error=0", exception.Message);

        firstAllocator.Dispose();
        Assert.Equal(port, thirdAllocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));
    }

    [Fact]
    public async Task ConcurrentReservations_StayExclusiveAndReleaseBeforeQueuedReacquisition()
    {
        var port = GetAvailablePort(30_001, 32_768);
        var allocators = Enumerable.Range(0, 8).Select(_ => new TestClusterPortAllocator()).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservations = allocators.Select(allocator => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                return allocator.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1) == port;
            }
            catch (InvalidOperationException exception)
            {
                Assert.Contains($"range [{port}, {port + 1}) after 100 attempts", exception.Message);
                Assert.Contains("reservation held by another process=0", exception.Message);
                Assert.Contains("reservation error=0", exception.Message);
                return false;
            }
        })).ToArray();

        try
        {
            start.SetResult();
            var results = await Task.WhenAll(reservations).WaitAsync(TestContext.Current.CancellationToken);
            var winner = Assert.Single(Enumerable.Range(0, results.Length), index => results[index]);
            allocators[winner].Dispose();

            using var verifier = new TestClusterPortAllocator();
            Assert.Equal(port, verifier.GetAvailableConsecutiveServerPorts([], port, port + 1, consecutivePortsToCheck: 1));
        }
        finally
        {
            start.TrySetResult();
            try
            {
                await Task.WhenAll(reservations);
            }
            finally
            {
                foreach (var allocator in allocators)
                {
                    allocator.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task MutexManager_LateAcquisitionAfterTimeout_ReleasesReservationAndAllowsSubsequentRequests()
    {
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeWorker = new ManualResetEventSlim();
        var blockNextAcquire = 1;
        using var manager = new TestClusterPortAllocator.MutexManager(
            TimeSpan.FromMilliseconds(100),
            () =>
            {
                if (Interlocked.Exchange(ref blockNextAcquire, 0) != 0)
                {
                    workerEntered.SetResult();
                    resumeWorker.Wait();
                }
            });
        var name = $"Orleans.TestClusterPortAllocatorTests.{Guid.NewGuid():N}";
        var cancellationToken = TestContext.Current.CancellationToken;
        var timedOutAcquisition = Task.Run(() => manager.Acquire(name), cancellationToken);

        try
        {
            await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() => timedOutAcquisition.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }
        finally
        {
            resumeWorker.Set();
        }

        var subsequentAcquisition = await Task.Run(() => manager.Acquire(name), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(TestClusterPortAllocator.MutexAcquisitionResult.Acquired, subsequentAcquisition.Result);
        manager.SignalRelease(name);
    }

    [Fact]
    public async Task MutexManager_TimedOutAcquisitionForOwnedName_PreservesOwnerReservation()
    {
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeWorker = new ManualResetEventSlim();
        var blockNextAcquire = 0;
        using var manager = new TestClusterPortAllocator.MutexManager(
            TimeSpan.FromMilliseconds(100),
            () =>
            {
                if (Interlocked.Exchange(ref blockNextAcquire, 0) != 0)
                {
                    workerEntered.SetResult();
                    resumeWorker.Wait();
                }
            });
        var name = $"Orleans.TestClusterPortAllocatorTests.{Guid.NewGuid():N}";
        var ownerAcquisition = manager.Acquire(name);
        Assert.Equal(TestClusterPortAllocator.MutexAcquisitionResult.Acquired, ownerAcquisition.Result);

        Volatile.Write(ref blockNextAcquire, 1);
        var cancellationToken = TestContext.Current.CancellationToken;
        var timedOutAcquisition = Task.Run(() => manager.Acquire(name), cancellationToken);
        try
        {
            await workerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() => timedOutAcquisition.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }
        finally
        {
            resumeWorker.Set();
        }

        var ownerReservation = await Task.Run(() => manager.Acquire(name), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(TestClusterPortAllocator.MutexAcquisitionResult.HeldByCurrentProcess, ownerReservation.Result);

        manager.SignalRelease(name);
        var subsequentAcquisition = await Task.Run(() => manager.Acquire(name), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(TestClusterPortAllocator.MutexAcquisitionResult.Acquired, subsequentAcquisition.Result);
        manager.SignalRelease(name);
    }

    [Fact]
    public void MutexManager_SignalReleaseAfterDispose_DoesNotThrow()
    {
        using var manager = new TestClusterPortAllocator.MutexManager(TimeSpan.FromSeconds(1));
        manager.Dispose();

        manager.SignalRelease($"Orleans.TestClusterPortAllocatorTests.{Guid.NewGuid():N}");
    }

    private static int GetAvailablePort(int rangeStart, int rangeEnd, int excludedPort = -1)
    {
        for (var port = rangeStart; port < rangeEnd; port++)
        {
            if (port == excludedPort)
            {
                continue;
            }

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException($"No available port was found in range [{rangeStart}, {rangeEnd}).");
    }
}
