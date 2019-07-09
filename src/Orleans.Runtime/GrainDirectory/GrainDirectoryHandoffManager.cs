using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Most methods of this class are synchronized since they might be called both
    /// from LocalGrainDirectory on CacheValidator.SchedulingContext and from RemoteGrainDirectory.
    /// </summary>
    internal class GrainDirectoryHandoffManager
    {
        private const int HANDOFF_CHUNK_SIZE = 500;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
        private const int MAX_OPERATION_DEQUEUE = 2;
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly LocalGrainDirectory localDirectory;
        private readonly IClusterMembershipService clusterMembership;
        private readonly IInternalGrainFactory grainFactory;
        private readonly HashSet<SiloAddress> receivingHandoffs = new HashSet<SiloAddress>();
        private readonly ILogger logger;
        private readonly Factory<GrainDirectoryPartition> createPartion;
        private readonly Queue<(string name, Func<Task> action)> pendingOperations = new Queue<(string name, Func<Task> action)>();
        private readonly AsyncLock executorLock = new AsyncLock();

        /// <summary>
        /// Whether or not this silo has handed off its directory to another silo.
        /// </summary>
        public bool HasPerformedHandoff { get; private set; }

        /// <summary>
        /// Whether or not this silo has received a directory split from another silo.
        /// </summary>
        public bool HasReceivedSplit { get; private set; }

        internal GrainDirectoryHandoffManager(
            ILocalSiloDetails localSiloDetails,
            LocalGrainDirectory localGrainDirectory,
            IClusterMembershipService clusterMembership,
            IInternalGrainFactory grainFactory,
            Factory<GrainDirectoryPartition> createPartion,
            ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<GrainDirectoryHandoffManager>();
            this.localSiloDetails = localSiloDetails;
            this.localDirectory = localGrainDirectory;
            this.clusterMembership = clusterMembership;
            this.grainFactory = grainFactory;
            this.createPartion = createPartion;
        }

        private async Task HandoffMyPartitionUponStop(Dictionary<GrainId, IGrainInfo> batchUpdate, SiloAddress destination, bool isFullCopy)
        {
            if (batchUpdate.Count == 0 || destination is null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug((isFullCopy ? "FULL" : "DELTA") + " handoff finished with empty delta (nothing to send)");
                return;
            }

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Sending {0} items to my {1}: (ring status is {2})", 
                batchUpdate.Count, destination, localDirectory.DirectoryMembershipSnapshot.ToDetailedString());

            var n = 0;
            var chunk = new Dictionary<GrainId, IGrainInfo>();

            // Note that batchUpdate will not change while this method is executing
            foreach (var pair in batchUpdate)
            {
                chunk[pair.Key] = pair.Value;
                n++;
                if ((n % HANDOFF_CHUNK_SIZE != 0) && (n != batchUpdate.Count))
                {
                    // If we haven't filled in a chunk yet, keep looping.
                    continue;
                }

                if (logger.IsEnabled(LogLevel.Debug)) this.logger.Debug("Sending handed off partition to " + destination);

                await this.localDirectory.Scheduler.RunOrQueueTask(
                    () => this.localDirectory.GetDirectoryReference(destination).AcceptHandoffPartition(
                            this.localSiloDetails.SiloAddress,
                            chunk,
                            isFullCopy),
                    this.localDirectory.RemoteGrainDirectory.SchedulingContext);

                chunk.Clear();

                // This is a quick temporary solution. We send a full copy by sending one chunk as a full copy and follow-on chunks as deltas.
                // Obviously, this will really mess up if there's a failure after the first chunk but before the others are sent, since on a
                // full copy receive the follower dumps all old data and replaces it with the new full copy. 
                // On the other hand, over time things should correct themselves, and of course, losing directory data isn't necessarily catastrophic.
                isFullCopy = false;
            }
        }

        internal void ProcessSiloRemoveEvent(SiloAddress removedSilo)
        {
            lock (this)
            {
                if (!receivingHandoffs.Remove(removedSilo)) return;
            }

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Processed silo remove event for " + removedSilo);
        }

        internal async Task ProcessSiloStoppingEvent(DirectoryMembershipSnapshot membershipSnapshot)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Processing silo stopping event");
            
            // take a copy of the current directory partition
            Dictionary<GrainId, IGrainInfo> batchUpdate = localDirectory.DirectoryPartition.GetItems();

            var destination = membershipSnapshot.FindPredecessor(this.localSiloDetails.SiloAddress);
            await HandoffMyPartitionUponStop(batchUpdate, destination, true);

            // This will cause remote write requests to be forwarded to the silo that will become the new owner.
            // Requests might bounce back and forth for a while as membership stabilizes, but they will either be served by the
            // new owner of the grain, or will wind up failing. In either case, we avoid requests succeeding at this silo after we've
            // completed handoff, which could cause them to not get handed off to the new owner.
            this.HasPerformedHandoff = true;
        }

        internal void ProcessSiloAddEvent(DirectoryMembershipSnapshot membershipSnapshot, SiloAddress addedSilo)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Processing silo add event for " + addedSilo);
                
                // check if this is our successor (i.e., if I should hold this silo's copy)
                // (if yes, adjust local and/or copied directory partitions by splitting them between old successors and the new one)
                // NOTE: We need to move part of our local directory to the new silo if it is an immediate successor.
                var successor = membershipSnapshot.FindSuccessor(this.localSiloDetails.SiloAddress);

                if (successor is null || !successor.Equals(addedSilo))
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug($"{addedSilo} is not one of my successors.");
                    return;
                }

                // split my local directory and send to my new immediate successor his share
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Splitting my partition between me and " + addedSilo);
                GrainDirectoryPartition splitPart = localDirectory.DirectoryPartition.Split(
                    grain =>
                    {
                        var s = membershipSnapshot.CalculateGrainDirectoryPartition(grain);
                        return (s != null) && !this.localSiloDetails.SiloAddress.Equals(s);
                    }, false);
                List<ActivationAddress> splitPartListSingle = splitPart.ToListOfActivations(true);
                List<ActivationAddress> splitPartListMulti = splitPart.ToListOfActivations(false);

                EnqueueOperation(
                    $"{nameof(ProcessSiloAddEvent)}({addedSilo})",
                    () => ProcessAddedSiloAsync(addedSilo, splitPartListSingle, splitPartListMulti));
            }
        }

        private async Task ProcessAddedSiloAsync(
            SiloAddress addedSilo,
            List<ActivationAddress> splitPartListSingle,
            List<ActivationAddress> splitPartListMulti)
        {
            if (localDirectory.DirectoryMembershipSnapshot.ClusterMembership.GetSiloStatus(addedSilo) == SiloStatus.Active)
            {
                if (splitPartListSingle.Count > 0)
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Sending " + splitPartListSingle.Count + " single activation entries to " + addedSilo);
                }

                if (splitPartListMulti.Count > 0)
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Sending " + splitPartListMulti.Count + " entries to " + addedSilo);
                }

                await localDirectory.GetDirectoryReference(addedSilo).AcceptSplitPartition(splitPartListSingle, splitPartListMulti);
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Warning)) logger.LogWarning("Silo " + addedSilo + " is no longer active and therefore cannot receive this partition split");
                return;
            }

            if (splitPartListSingle.Count > 0)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Removing " + splitPartListSingle.Count + " single activation after partition split");

                splitPartListSingle.ForEach(
                    activationAddress =>
                        localDirectory.DirectoryPartition.RemoveGrain(activationAddress.Grain));
            }

            if (splitPartListMulti.Count > 0)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Removing " + splitPartListMulti.Count + " multiple activation after partition split");

                splitPartListMulti.ForEach(
                    activationAddress =>
                        localDirectory.DirectoryPartition.RemoveGrain(activationAddress.Grain));
            }
        }

        internal void AcceptExistingRegistrations(List<ActivationAddress> singleActivations, List<ActivationAddress> multiActivations)
        {
            this.EnqueueOperation(
                nameof(AcceptExistingRegistrations),
                () => AcceptExistingRegistrationsAsync(singleActivations, multiActivations));
        }

        private async Task AcceptExistingRegistrationsAsync(List<ActivationAddress> singleActivations, List<ActivationAddress> multiActivations)
        {
            if (this.logger.IsEnabled(LogLevel.Debug))
            {
                this.logger.LogDebug(
                    $"{nameof(AcceptExistingRegistrations)}: accepting {singleActivations?.Count ?? 0} single-activation registrations and {multiActivations?.Count ?? 0} multi-activation registrations.");
            }

            if (singleActivations != null && singleActivations.Count > 0)
            {
                var tasks = singleActivations.Select(addr => this.localDirectory.RegisterAsync(addr, true, 1, skipInitializationCheck: true)).ToArray();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception exception)
                {
                    if (this.logger.IsEnabled(LogLevel.Warning))
                        this.logger.LogWarning($"Exception registering activations in {nameof(AcceptExistingRegistrations)}: {LogFormatter.PrintException(exception)}");
                    throw;
                }
                finally
                {
                    Dictionary<SiloAddress, List<ActivationAddress>> duplicates = new Dictionary<SiloAddress, List<ActivationAddress>>();
                    for (var i = tasks.Length - 1; i >= 0; i--)
                    {
                        // Retry failed tasks next time.
                        if (tasks[i].Status != TaskStatus.RanToCompletion) continue;

                        // Record the applications which lost the registration race (duplicate activations).
                        var winner = await tasks[i];
                        if (!winner.Address.Equals(singleActivations[i]))
                        {
                            var duplicate = singleActivations[i];

                            if (!duplicates.TryGetValue(duplicate.Silo, out var activations))
                            {
                                activations = duplicates[duplicate.Silo] = new List<ActivationAddress>(1);
                            }

                            activations.Add(duplicate);
                        }

                        // Remove tasks which completed.
                        singleActivations.RemoveAt(i);
                    }

                    // Destroy any duplicate activations.
                    DestroyDuplicateActivations(duplicates);
                }
            }

            // Multi-activation grains are much simpler because there is no need for duplicate activation logic.
            if (multiActivations != null && multiActivations.Count > 0)
            {
                var tasks = multiActivations.Select(addr => this.localDirectory.RegisterAsync(addr, false, 1, skipInitializationCheck: true)).ToArray();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception exception)
                {
                    if (this.logger.IsEnabled(LogLevel.Warning))
                        this.logger.LogWarning($"Exception registering activations in {nameof(AcceptExistingRegistrations)}: {LogFormatter.PrintException(exception)}");
                    throw;
                }
                finally
                {
                    for (var i = tasks.Length - 1; i >= 0; i--)
                    {
                        // Retry failed tasks next time.
                        if (tasks[i].Status != TaskStatus.RanToCompletion) continue;

                        // Remove tasks which completed.
                        multiActivations.RemoveAt(i);
                    }
                }
            }

            this.HasReceivedSplit = true;
        }

        internal void AcceptHandoffPartition(SiloAddress source, Dictionary<GrainId, IGrainInfo> partition, bool isFullCopy)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Got request to register " + (isFullCopy ? "FULL" : "DELTA") + " directory partition with " + partition.Count + " elements from " + source);

                var thisChunk = this.createPartion();
                thisChunk.Set(partition);

                this.receivingHandoffs.Add(source);

                // Immediately merge the remote partition with the local directory partition so that we can serve requests
                // using the data.
                var duplicates = this.localDirectory.DirectoryPartition.Merge(thisChunk);
                this.DestroyDuplicateActivations(duplicates);
                localDirectory.GsiActivationMaintainer.TrackDoubtfulGrains(partition);
            }
        }

        public bool HasAcceptedHandoffForSilo(SiloAddress source)
        {
            if (source is null) return false;

            lock (this)
            {
                return receivingHandoffs.Contains(source);
            }
        }

        private void DestroyDuplicateActivations(Dictionary<SiloAddress, List<ActivationAddress>> duplicates)
        {
            if (duplicates == null || duplicates.Count == 0) return;
            this.EnqueueOperation(
                nameof(DestroyDuplicateActivations),
                () => DestroyDuplicateActivationsAsync(duplicates));
        }

        private async Task DestroyDuplicateActivationsAsync(Dictionary<SiloAddress, List<ActivationAddress>> duplicates)
        {
            while (duplicates.Count > 0)
            {
                var pair = duplicates.FirstOrDefault();
                if (this.clusterMembership.CurrentSnapshot.GetSiloStatus(pair.Key) == SiloStatus.Active)
                {
                    if (this.logger.IsEnabled(LogLevel.Debug))
                    {
                        this.logger.LogDebug(
                            $"{nameof(DestroyDuplicateActivations)} will destroy {duplicates.Count} duplicate activations on silo {pair.Key}: {string.Join("\n * ", pair.Value.Select(_ => _))}");
                    }

                    var remoteCatalog = this.grainFactory.GetSystemTarget<ICatalog>(Constants.CatalogId, pair.Key);
                    await remoteCatalog.DeleteActivations(pair.Value);
                }

                duplicates.Remove(pair.Key);
            }
        }

        private void EnqueueOperation(string name, Func<Task> action)
        {
            lock (this)
            {
                this.pendingOperations.Enqueue((name, action));
                if (this.pendingOperations.Count <= 2)
                {
                    this.localDirectory.Scheduler.QueueTask(this.ExecutePendingOperations, this.localDirectory.RemoteGrainDirectory.SchedulingContext);
                }
            }
        }

        private async Task ExecutePendingOperations()
        {
            using (await executorLock.LockAsync())
            {
                var dequeueCount = 0;
                while (true)
                {
                    // Get the next operation, or exit if there are none.
                    (string Name, Func<Task> Action) op;
                    lock (this)
                    {
                        if (this.pendingOperations.Count == 0) break;

                        op = this.pendingOperations.Peek();
                    }

                    dequeueCount++;

                    try
                    {
                        await op.Action();
                        // Success, reset the dequeue count
                        dequeueCount = 0;
                    }
                    catch (Exception exception)
                    {
                        if (dequeueCount < MAX_OPERATION_DEQUEUE)
                        {
                            if (this.logger.IsEnabled(LogLevel.Warning))
                                this.logger.LogWarning($"{op.Name} failed, will be retried: {LogFormatter.PrintException(exception)}.");
                            await Task.Delay(RetryDelay);
                        }
                        else
                        {
                            if (this.logger.IsEnabled(LogLevel.Warning))
                                this.logger.LogWarning($"{op.Name} failed, will NOT be retried: {LogFormatter.PrintException(exception)}");
                        }
                    }
                    if (dequeueCount == 0 || dequeueCount >= MAX_OPERATION_DEQUEUE)
                    {
                        lock (this)
                        {
                            // Remove the operation from the queue if it was a success
                            // or if we tried too many times
                            this.pendingOperations.Dequeue();
                        }
                    }
                }
            }
        }
    }
}
