using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Tester.StreamingTests
{
    /// <summary>
    /// Test queue balancer that uses a grain-based lease manager for testing pluggable queue balancer functionality.
    /// This balancer responds to cluster membership changes to properly re-balance queues when silos join or leave.
    /// </summary>
    public class LeaseBasedQueueBalancerForTest : QueueBalancerBase
    {
        private readonly string _name;
        private readonly ILeaseManagerGrain _leaseManagerGrain;
        private readonly string _id;
        private readonly object _lock = new();
        private List<QueueId> _ownedQueues = [];
        private int _allQueuesCount;
        private bool _initialized;

        public LeaseBasedQueueBalancerForTest(IServiceProvider serviceProvider, string name)
            : base(serviceProvider, serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<LeaseBasedQueueBalancerForTest>())
        {
            _name = name;
            var grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();
            _leaseManagerGrain = grainFactory.GetGrain<ILeaseManagerGrain>(name);
            _id = $"{name}-{Guid.NewGuid()}";
        }

        public override async Task Initialize(IStreamQueueMapper queueMapper)
        {
            var allQueues = queueMapper.GetAllQueues().ToList();
            _allQueuesCount = allQueues.Count;
            
            // Set up the lease manager with all queues
            await _leaseManagerGrain.SetQueuesAsLeases(allQueues);
            
            // Initialize the base class (starts listening for cluster membership changes)
            await base.Initialize(queueMapper);
            _initialized = true;
            
            // Acquire initial leases
            await AcquireLeasesToMeetResponsibility();
        }

        public override IEnumerable<QueueId> GetMyQueues()
        {
            lock (_lock)
            {
                return _ownedQueues.ToList();
            }
        }

        protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos)
        {
            if (!_initialized || Cancellation.IsCancellationRequested)
                return;

            // Re-balance queues when membership changes
            _ = RebalanceAsync(activeSilos.Count);
        }

        private async Task RebalanceAsync(int activeSiloCount)
        {
            try
            {
                // Release excess queues first if we have too many
                var responsibility = _allQueuesCount / Math.Max(1, activeSiloCount);
                
                lock (_lock)
                {
                    if (_ownedQueues.Count > responsibility)
                    {
                        // Release excess queues (don't actually release to lease manager, just stop owning them)
                        var excessCount = _ownedQueues.Count - responsibility;
                        for (int i = 0; i < excessCount; i++)
                        {
                            var queueToRelease = _ownedQueues[_ownedQueues.Count - 1];
                            _ownedQueues.RemoveAt(_ownedQueues.Count - 1);
                            // Fire and forget the release
                            _ = _leaseManagerGrain.Release(queueToRelease);
                        }
                    }
                }

                // Try to acquire more if needed
                await AcquireLeasesToMeetResponsibility();
                
                // Notify listeners of the change
                await NotifyListeners();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error rebalancing queues");
            }
        }

        private async Task AcquireLeasesToMeetResponsibility()
        {
            var responsibility = await _leaseManagerGrain.GetLeaseResposibility();
            
            int currentCount;
            lock (_lock)
            {
                currentCount = _ownedQueues.Count;
            }
            
            var queuesTryAcquire = responsibility - currentCount;
            
            for (int i = 0; i < queuesTryAcquire; i++)
            {
                try
                {
                    var queue = await _leaseManagerGrain.Acquire();
                    lock (_lock)
                    {
                        _ownedQueues.Add(queue);
                    }
                }
                catch (KeyNotFoundException)
                {
                    // No more queues available, that's okay
                    break;
                }
            }

            // Record the final responsibility for test verification
            int finalCount;
            lock (_lock)
            {
                finalCount = _ownedQueues.Count;
            }
            await _leaseManagerGrain.RecordBalancerResponsibility(_id, finalCount);
        }
    }
}
