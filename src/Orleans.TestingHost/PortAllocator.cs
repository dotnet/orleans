using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{
    public class TestClusterPortAllocator : ITestClusterPortAllocator
    {
        private bool disposed;
        private readonly object lockObj = new object();
        private readonly Dictionary<int, string> allocatedPorts = new Dictionary<int, string>();

        public (int, int) AllocateConsecutivePortPairs(int numPorts = 5)
        {
            // Evaluate current system tcp connections
            IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();

            // each returned port in the pair will have to have at least this amount of available ports following it

            return (GetAvailableConsecutiveServerPorts(tcpConnInfoArray, 22300, 30000, numPorts),
                GetAvailableConsecutiveServerPorts(tcpConnInfoArray, 40000, 50000, numPorts));
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            lock (lockObj)
            {
                if (disposed)
                {
                    return;
                }

                foreach (var pair in allocatedPorts)
                {
                    MutexManager.Instance.Release(pair.Value);
                }

                allocatedPorts.Clear();
                disposed = true;
            }
        }

        ~TestClusterPortAllocator()
        {
            Dispose(false);
        }

        private int GetAvailableConsecutiveServerPorts(IPEndPoint[] tcpConnInfoArray, int portStartRange, int portEndRange, int consecutivePortsToCheck)
        {
            const int MaxAttempts = 10;

            var allocations = new List<(int Port, string Mutex)>();

            for (int attempts = 0; attempts < MaxAttempts; attempts++)
            {
                int basePort = ThreadSafeRandom.Next(portStartRange, portEndRange);

                // get ports in buckets, so we don't interfere with parallel runs of this same function
                basePort = basePort - (basePort % consecutivePortsToCheck);
                int endPort = basePort + consecutivePortsToCheck;

                // make sure none of the ports in the sub range are in use
                if (tcpConnInfoArray.All(endpoint => endpoint.Port < basePort || endpoint.Port >= endPort))
                {
                    for (var i = 0; i < consecutivePortsToCheck; i++)
                    {
                        var port = basePort + i;
                        var name = $"Global.TestCluster.{port.ToString(CultureInfo.InvariantCulture)}";
                        if (MutexManager.Instance.Acquire(name, 500))
                        {
                            allocations.Add((port, name));
                        }
                        else
                        {
                            foreach (var allocation in allocations)
                            {
                                MutexManager.Instance.Release(allocation.Mutex);
                            }

                            allocations.Clear();
                            break;
                        }
                    }

                    if (allocations.Count == 0)
                    {
                        // Try a different range.
                        continue;
                    }

                    lock (lockObj)
                    {
                        foreach (var allocation in allocations)
                        {
                            allocatedPorts[allocation.Port] = allocation.Mutex;
                        }
                    }

                    return basePort;
                }
            }

            throw new InvalidOperationException("Cannot find enough free ports to spin up a cluster");
        }

        private class MutexManager
        {
            private readonly Dictionary<string, Mutex> _mutexes = new Dictionary<string, Mutex>();
            private readonly BlockingCollection<Action> _workItems = new BlockingCollection<Action>();
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
            }

            public bool Acquire(string name, int millisecondsTimeout)
            {
                var completion = new TaskCompletionSource<bool>(TaskContinuationOptions.RunContinuationsAsynchronously);
                _workItems.Add(() =>
                {
                    try
                    {
                        if (!_mutexes.TryGetValue(name, out var value))
                        {
                            _mutexes[name] = value = new Mutex(false, name);
                        }

                        completion.TrySetResult(value.WaitOne(millisecondsTimeout));
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });

                return completion.Task.GetAwaiter().GetResult();
            }

            public bool Release(string name)
            {
                var completion = new TaskCompletionSource<bool>(TaskContinuationOptions.RunContinuationsAsynchronously);
                _workItems.Add(() =>
                {
                    try
                    {
                        if (!_mutexes.TryGetValue(name, out var value))
                        {
                            completion.TrySetResult(false);
                            return;
                        }

                        _mutexes.Remove(name);
                        value.ReleaseMutex();
                        completion.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });

                return completion.Task.GetAwaiter().GetResult();
            }

            private void Run()
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
        }
    }
}