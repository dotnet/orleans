using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

        return (GetAvailableConsecutiveServerPorts(tcpConnInfoArray, 22300, 30000, numPorts),
            GetAvailableConsecutiveServerPorts(tcpConnInfoArray, 40000, 50000, numPorts));
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

        var stopwatch = Stopwatch.StartNew();
        var allocations = new List<(int Port, string Mutex)>();
        var listenerRejections = 0;
        var socketRejections = 0;
        var localReservationRejections = 0;
        var externalReservationRejections = 0;
        int? lastListenerPort = null;
        (int Port, SocketError SocketError, int NativeErrorCode)? lastSocketFailure = null;
        int? lastLocallyReservedPort = null;
        int? lastExternallyReservedPort = null;

        for (var attempts = 0; attempts < MaxAttempts; attempts++)
        {
            var basePort = Random.Shared.Next(portStartRange, portEndRange);

            // get ports in buckets, so we don't interfere with parallel runs of this same function
            basePort = basePort - basePort % consecutivePortsToCheck;
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
                MutexAcquisitionResult acquisitionResult;
                try
                {
                    acquisitionResult = MutexManager.Instance.Acquire(name);
                }
                catch
                {
                    ReleaseAllocations(allocations);
                    throw;
                }

                if (acquisitionResult == MutexAcquisitionResult.Acquired)
                {
                    allocations.Add((port, name));
                }
                else
                {
                    if (acquisitionResult == MutexAcquisitionResult.HeldByCurrentProcess)
                    {
                        localReservationRejections++;
                        lastLocallyReservedPort = port;
                    }
                    else
                    {
                        externalReservationRejections++;
                        lastExternallyReservedPort = port;
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
        throw new InvalidOperationException(
            $"Cannot find {consecutivePortsToCheck} consecutive free ports in range [{portStartRange}, {portEndRange}) after {MaxAttempts} attempts in {stopwatch.ElapsedMilliseconds} ms. "
            + $"Rejected candidate ranges: active TCP listener={listenerRejections} (last port: {lastListenerPort?.ToString(CultureInfo.InvariantCulture) ?? "none"}), "
            + $"socket bind failure={socketRejections} (last failure: {lastSocketFailureDescription}), "
            + $"reservation held by this process={localReservationRejections} (last port: {lastLocallyReservedPort?.ToString(CultureInfo.InvariantCulture) ?? "none"}), "
            + $"reservation held by another process={externalReservationRejections} (last port: {lastExternallyReservedPort?.ToString(CultureInfo.InvariantCulture) ?? "none"}).");

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
    }

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

        public MutexAcquisitionResult Acquire(string name)
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

            error?.Throw();
            return result[0];
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
