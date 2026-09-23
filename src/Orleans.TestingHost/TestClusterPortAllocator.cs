using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Orleans.TestingHost;

/// <summary>
/// Default <see cref="ITestClusterPortAllocator"/> implementation, which tries to allocate unused ports.
/// </summary>
public class TestClusterPortAllocator : ITestClusterPortAllocator
{
    internal const int GatewayPortRangeStart = 12_000;
    internal const int GatewayPortRangeEnd = 22_000;
    internal const int SiloPortRangeStart = 22_300;
    internal const int SiloPortRangeEnd = 30_000;

    private bool _disposed;
#if NET9_0_OR_GREATER
    private readonly Lock _lockObj = new();
#else
    private readonly object _lockObj = new();
#endif
    private readonly Dictionary<int, string> _allocatedPorts = [];

    /// <inheritdoc />
    public (int, int) AllocateConsecutivePortPairs(int numPorts = 5)
    {
        // Evaluate current system tcp connections
        var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
        var tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();

        // each returned port in the pair will have to have at least this amount of available ports following it

        // These server-port ranges sit below the default dynamic client-port ranges on supported operating systems.
        return (GetAvailableConsecutiveServerPorts(tcpConnInfoArray, SiloPortRangeStart, SiloPortRangeEnd, numPorts),
            GetAvailableConsecutiveServerPorts(tcpConnInfoArray, GatewayPortRangeStart, GatewayPortRangeEnd, numPorts));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="disposing"><see langword="true" /> to release both managed and unmanaged resources; <see langword="false" /> to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        lock (_lockObj)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var pair in _allocatedPorts)
            {
                MutexManager.Instance.SignalRelease(pair.Value);
            }

            _allocatedPorts.Clear();
            _disposed = true;
        }
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="TestClusterPortAllocator"/> class.
    /// </summary>
    ~TestClusterPortAllocator()
    {
        Dispose(false);
    }

    internal int GetAvailableConsecutiveServerPorts(IPEndPoint[] tcpConnInfoArray, int portStartRange, int portEndRange, int consecutivePortsToCheck)
    {
        const int MaxAttempts = 100;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutivePortsToCheck);
        var firstBasePort = portStartRange;
        var remainder = firstBasePort % consecutivePortsToCheck;
        if (remainder != 0)
        {
            firstBasePort += consecutivePortsToCheck - remainder;
        }

        var bucketCount = (portEndRange - firstBasePort) / consecutivePortsToCheck;
        if (bucketCount <= 0)
        {
            throw new ArgumentException("The requested port range cannot contain the required consecutive ports.");
        }

        var stopwatch = Stopwatch.StartNew();
        var allocations = new List<(int Port, string Mutex)>();
        var listenerRejections = 0;
        var socketRejections = 0;
        var localReservationRejections = 0;
        var externalReservationRejections = 0;
        var reservationErrors = 0;
        int? lastListenerPort = null;
        (int Port, SocketError SocketError, int NativeErrorCode)? lastSocketFailure = null;
        int? lastLocallyReservedPort = null;
        int? lastExternallyReservedPort = null;
        Exception? lastReservationError = null;

        for (var attempts = 0; attempts < MaxAttempts; attempts++)
        {
            // Use aligned buckets so parallel allocators probe disjoint consecutive ranges.
            var basePort = firstBasePort + Random.Shared.Next(bucketCount) * consecutivePortsToCheck;
            var endPort = basePort + consecutivePortsToCheck;

            // make sure none of the ports in the sub range are in use
            var activeListener = tcpConnInfoArray.FirstOrDefault(endpoint => endpoint.Port >= basePort && endpoint.Port < endPort);
            if (activeListener is not null)
            {
                listenerRejections++;
                lastListenerPort = activeListener.Port;
                continue;
            }

            var portsAvailable = true;
            for (var i = 0; i < consecutivePortsToCheck; i++)
            {
                var port = basePort + i;
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                }
                catch (SocketException exception)
                {
                    socketRejections++;
                    lastSocketFailure = (port, exception.SocketErrorCode, exception.NativeErrorCode);
                    portsAvailable = false;
                    break;
                }
            }

            if (!portsAvailable)
            {
                continue;
            }

            for (var i = 0; i < consecutivePortsToCheck; i++)
            {
                var port = basePort + i;
                var name = $"Global.TestCluster.{port.ToString(CultureInfo.InvariantCulture)}";
                MutexAcquisition acquisition;
                try
                {
                    acquisition = MutexManager.Instance.Acquire(name);
                }
                catch
                {
                    ReleaseAllocations(allocations);
                    throw;
                }

                if (acquisition.Result == MutexAcquisitionResult.Acquired)
                {
                    allocations.Add((port, name));
                }
                else
                {
                    if (acquisition.Result == MutexAcquisitionResult.HeldByCurrentProcess)
                    {
                        localReservationRejections++;
                        lastLocallyReservedPort = port;
                    }
                    else if (acquisition.Result == MutexAcquisitionResult.HeldByAnotherProcess)
                    {
                        externalReservationRejections++;
                        lastExternallyReservedPort = port;
                    }
                    else
                    {
                        reservationErrors++;
                        lastReservationError = acquisition.Error;
                    }

                    ReleaseAllocations(allocations);
                    break;
                }
            }

            if (allocations.Count == 0)
            {
                // Try a different range.
                continue;
            }

            lock (_lockObj)
            {
                foreach (var allocation in allocations)
                {
                    _allocatedPorts[allocation.Port] = allocation.Mutex;
                }
            }

            return basePort;
        }

        var lastSocketFailureDescription = lastSocketFailure is { } socketFailure
            ? $"{socketFailure.Port} ({socketFailure.SocketError}, native error {socketFailure.NativeErrorCode})"
            : "none";
        var lastReservationErrorDescription = lastReservationError is { } reservationError
            ? $"{reservationError.GetType().Name}: {reservationError.Message}"
            : "none";
        throw new InvalidOperationException(
            $"Cannot find {consecutivePortsToCheck} consecutive free ports in range [{portStartRange}, {portEndRange}) after {MaxAttempts} attempts in {stopwatch.ElapsedMilliseconds} ms. "
            + $"Rejected candidate ranges: active TCP listener={listenerRejections} (last port: {lastListenerPort?.ToString(CultureInfo.InvariantCulture) ?? "none"}), "
            + $"socket bind failure={socketRejections} (last failure: {lastSocketFailureDescription}), "
            + $"reservation held by this process={localReservationRejections} (last port: {lastLocallyReservedPort?.ToString(CultureInfo.InvariantCulture) ?? "none"}), "
            + $"reservation held by another process={externalReservationRejections} (last port: {lastExternallyReservedPort?.ToString(CultureInfo.InvariantCulture) ?? "none"}), "
            + $"reservation error={reservationErrors} (last error: {lastReservationErrorDescription}).",
            lastReservationError);

        static void ReleaseAllocations(List<(int Port, string Mutex)> allocations)
        {
            foreach (var allocation in allocations)
            {
                MutexManager.Instance.SignalRelease(allocation.Mutex);
            }

            allocations.Clear();
        }
    }

    internal enum MutexAcquisitionResult
    {
        Acquired,
        HeldByCurrentProcess,
        HeldByAnotherProcess,
        Error,
    }

    private readonly record struct MutexAcquisition(MutexAcquisitionResult Result, Exception? Error = null);

    private class MutexManager
    {
        private readonly Dictionary<string, Mutex> _mutexes = [];
        private readonly BlockingCollection<Action> _workItems = [];
        private readonly Thread _thread;

        public static MutexManager Instance { get; } = new MutexManager();

        private MutexManager()
        {
            _thread = new Thread(Run)
            {
                Name = "MutexManager.Worker",
                IsBackground = true,
            };
            _thread.Start();
            AppDomain.CurrentDomain.DomainUnload += this.OnAppDomainUnload;
        }

        private void OnAppDomainUnload(object? sender, EventArgs e) => Shutdown();

        private void Shutdown()
        {
            _workItems.CompleteAdding();
            _thread.Join();
        }

        public MutexAcquisition Acquire(string name)
        {
            var result = new[] { MutexAcquisitionResult.HeldByCurrentProcess };
            var signal = new ManualResetEventSlim(initialState: false);
            ExceptionDispatchInfo? error = null;
            _workItems.Add(() =>
            {
                try
                {
                    if (!_mutexes.ContainsKey(name))
                    {
                        Mutex? mutex = new(false, name);
                        var acquired = false;
                        try
                        {
                            try
                            {
                                acquired = mutex.WaitOne(500);
                            }
                            catch (AbandonedMutexException)
                            {
                                acquired = true;
                            }

                            if (acquired)
                            {
                                // Acquired
                                _mutexes[name] = mutex;
                                mutex = null;
                                result[0] = MutexAcquisitionResult.Acquired;
                            }
                            else
                            {
                                result[0] = MutexAcquisitionResult.HeldByAnotherProcess;
                            }
                        }
                        finally
                        {
                            if (mutex is not null)
                            {
                                try
                                {
                                    if (acquired)
                                    {
                                        mutex.ReleaseMutex();
                                    }
                                }
                                finally
                                {
                                    mutex.Close();
                                }
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                {
                    result[0] = MutexAcquisitionResult.Error;
                    error = ExceptionDispatchInfo.Capture(exception);
                }
                catch (Exception exception)
                {
                    error = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    signal.Set();
                }
            });

            if (!signal.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out while waiting for MutexManager to acquire mutex.");
            }

            if (result[0] != MutexAcquisitionResult.Error)
            {
                error?.Throw();
            }

            return new MutexAcquisition(result[0], error?.SourceException);
        }

        public void SignalRelease(string name)
        {
            if (_workItems.IsAddingCompleted) return;

            try
            {
                _workItems.Add(() =>
                {
                    if (_mutexes.Remove(name, out var value))
                    {
                        value.ReleaseMutex();
                        value.Close();
                    }
                });
            }
            catch
            {
            }
        }

        private void Run()
        {
            try
            {
                foreach (var action in _workItems.GetConsumingEnumerable())
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                foreach (var mutex in _mutexes.Values)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch { }
                    finally
                    {
                        mutex.Close();
                    }
                }

                _mutexes.Clear();
            }
        }
    }
}
