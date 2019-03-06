using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        private readonly LocalGrainDirectory localDirectory;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly Dictionary<SiloAddress, GrainDirectoryPartition> directoryPartitionsMap;
        private readonly List<SiloAddress> silosHoldingMyPartition;
        private readonly Dictionary<SiloAddress, Task> lastPromise;
        private readonly ILogger logger;
        private readonly Factory<GrainDirectoryPartition> createPartion;
        private readonly Queue<Operation> pendingOperations = new Queue<Operation>();
        private readonly AsyncLock executorLock = new AsyncLock();

        internal GrainDirectoryHandoffManager(
            LocalGrainDirectory localDirectory,
            ISiloStatusOracle siloStatusOracle,
            IInternalGrainFactory grainFactory,
            Factory<GrainDirectoryPartition> createPartion,
            ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<GrainDirectoryHandoffManager>();
            this.localDirectory = localDirectory;
            this.siloStatusOracle = siloStatusOracle;
            this.grainFactory = grainFactory;
            this.createPartion = createPartion;
            directoryPartitionsMap = new Dictionary<SiloAddress, GrainDirectoryPartition>();
            silosHoldingMyPartition = new List<SiloAddress>();
            lastPromise = new Dictionary<SiloAddress, Task>();
        }

        internal List<ActivationAddress> GetHandedOffInfo(GrainId grain)
        {
            lock (this)
            {
                foreach (var partition in directoryPartitionsMap.Values)
                {
                    var result = partition.LookUpActivations(grain);
                    if (result.Addresses != null)
                        return result.Addresses;
                }
            }
            return null;
        }

        private async Task HandoffMyPartitionUponStop(Dictionary<GrainId, IGrainInfo> batchUpdate, List<SiloAddress> silosHoldingMyPartitionCopy, bool isFullCopy)
        {
            if (batchUpdate.Count == 0 || silosHoldingMyPartitionCopy.Count == 0)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug((isFullCopy ? "FULL" : "DELTA") + " handoff finished with empty delta (nothing to send)");
                return;
            }

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Sending {0} items to my {1}: (ring status is {2})", 
                batchUpdate.Count, silosHoldingMyPartitionCopy.ToStrings(), localDirectory.RingStatusToString());

            var tasks = new List<Task>();

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

                foreach (SiloAddress silo in silosHoldingMyPartitionCopy)
                {
                    SiloAddress captureSilo = silo;
                    Dictionary<GrainId, IGrainInfo> captureChunk = chunk;
                    bool captureIsFullCopy = isFullCopy;
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Sending handed off partition to " + captureSilo);

                    Task pendingRequest;
                    if (lastPromise.TryGetValue(captureSilo, out pendingRequest))
                    {
                        try
                        {
                            await pendingRequest;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    Task task = localDirectory.Scheduler.RunOrQueueTask(
                                () => localDirectory.GetDirectoryReference(captureSilo).AcceptHandoffPartition(
                                        localDirectory.MyAddress,
                                        captureChunk,
                                        captureIsFullCopy),
                                localDirectory.RemoteGrainDirectory.SchedulingContext);
                    lastPromise[captureSilo] = task;
                    tasks.Add(task);
                }
                // We need to use a new Dictionary because the call to AcceptHandoffPartition, which reads the current Dictionary,
                // happens asynchronously (and typically after some delay).
                chunk = new Dictionary<GrainId, IGrainInfo>();

                // This is a quick temporary solution. We send a full copy by sending one chunk as a full copy and follow-on chunks as deltas.
                // Obviously, this will really mess up if there's a failure after the first chunk but before the others are sent, since on a
                // full copy receive the follower dumps all old data and replaces it with the new full copy. 
                // On the other hand, over time things should correct themselves, and of course, losing directory data isn't necessarily catastrophic.
                isFullCopy = false;
            }
            await Task.WhenAll(tasks);
        }

        internal void ProcessSiloRemoveEvent(SiloAddress removedSilo)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Processing silo remove event for " + removedSilo);

                // Reset our follower list to take the changes into account
                ResetFollowers();

                // check if this is one of our successors (i.e., if I hold this silo's copy)
                // (if yes, adjust local and/or handoffed directory partitions)
                if (!directoryPartitionsMap.ContainsKey(removedSilo)) return;

                // at least one predcessor should exist, which is me
                SiloAddress predecessor = localDirectory.FindPredecessors(removedSilo, 1)[0];
                Dictionary<SiloAddress, List<ActivationAddress>> duplicates;
                if (localDirectory.MyAddress.Equals(predecessor))
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Merging my partition with the copy of silo " + removedSilo);
                    // now I am responsible for this directory part
                    duplicates = localDirectory.DirectoryPartition.Merge(directoryPartitionsMap[removedSilo]);
                    // no need to send our new partition to all others, as they
                    // will realize the change and combine their copies without any additional communication (see below)
                }
                else
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Merging partition of " + predecessor + " with the copy of silo " + removedSilo);
                    // adjust copy for the predecessor of the failed silo
                    duplicates = directoryPartitionsMap[predecessor].Merge(directoryPartitionsMap[removedSilo]);
                }

                localDirectory.GsiActivationMaintainer.TrackDoubtfulGrains(directoryPartitionsMap[removedSilo].GetItems());
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Removed copied partition of silo " + removedSilo);
                directoryPartitionsMap.Remove(removedSilo);
                DestroyDuplicateActivations(duplicates);
            }
        }

        internal Task ProcessSiloStoppingEvent()
        {
            return ProcessSiloStoppingEvent_Impl();
        }

        private async Task ProcessSiloStoppingEvent_Impl()
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Processing silo stopping event");

            // As we're about to enter an async context further down, this is the latest opportunity to lock, modify and copy
            // silosHoldingMyPartition for use inside of HandoffMyPartitionUponStop
            List<SiloAddress> silosHoldingMyPartitionCopy;
            lock (this)
            {
                // Select our nearest predecessor to receive our hand-off, since that's the silo that will wind up owning our partition (assuming
                // that it doesn't also fail and that no other silo joins during the transition period).
                if (silosHoldingMyPartition.Count == 0)
                {
                    silosHoldingMyPartition.AddRange(localDirectory.FindPredecessors(localDirectory.MyAddress, 1));
                }

                silosHoldingMyPartitionCopy = silosHoldingMyPartition.ToList();
            }

            // take a copy of the current directory partition
            Dictionary<GrainId, IGrainInfo> batchUpdate = localDirectory.DirectoryPartition.GetItems();

            await HandoffMyPartitionUponStop(batchUpdate, silosHoldingMyPartitionCopy, true);
        }

        internal void ProcessSiloAddEvent(SiloAddress addedSilo)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Processing silo add event for " + addedSilo);

                // Reset our follower list to take the changes into account
                ResetFollowers();

                // check if this is one of our successors (i.e., if I should hold this silo's copy)
                // (if yes, adjust local and/or copied directory partitions by splitting them between old successors and the new one)
                // NOTE: We need to move part of our local directory to the new silo if it is an immediate successor.
                List<SiloAddress> successors = localDirectory.FindSuccessors(localDirectory.MyAddress, 1);
                if (!successors.Contains(addedSilo))
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug($"{addedSilo} is not one of my successors.");
                    return;
                }

                // check if this is an immediate successor
                if (successors[0].Equals(addedSilo))
                {
                    // split my local directory and send to my new immediate successor his share
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Splitting my partition between me and " + addedSilo);
                    GrainDirectoryPartition splitPart = localDirectory.DirectoryPartition.Split(
                        grain =>
                        {
                            var s = localDirectory.CalculateGrainDirectoryPartition(grain);
                            return (s != null) && !localDirectory.MyAddress.Equals(s);
                        }, false);
                    List<ActivationAddress> splitPartListSingle = splitPart.ToListOfActivations(true);
                    List<ActivationAddress> splitPartListMulti = splitPart.ToListOfActivations(false);

                    EnqueueOperation(
                        $"{nameof(ProcessSiloAddEvent)}({addedSilo})",
                        () => ProcessAddedSiloAsync(addedSilo, splitPartListSingle, splitPartListMulti));
                }
                else
                {
                    // adjust partitions by splitting them accordingly between new and old silos
                    SiloAddress predecessorOfNewSilo = localDirectory.FindPredecessors(addedSilo, 1)[0];
                    if (!directoryPartitionsMap.ContainsKey(predecessorOfNewSilo))
                    {
                        // we should have the partition of the predcessor of our new successor
                        logger.Warn(ErrorCode.DirectoryPartitionPredecessorExpected, "This silo is expected to hold directory partition of " + predecessorOfNewSilo);
                    }
                    else
                    {
                        if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Splitting partition of " + predecessorOfNewSilo + " and creating a copy for " + addedSilo);
                        GrainDirectoryPartition splitPart = directoryPartitionsMap[predecessorOfNewSilo].Split(
                            grain =>
                            {
                                // Need to review the 2nd line condition.
                                var s = localDirectory.CalculateGrainDirectoryPartition(grain);
                                return (s != null) && !predecessorOfNewSilo.Equals(s);
                            }, true);
                        directoryPartitionsMap[addedSilo] = splitPart;
                    }
                }

                // remove partition of one of the old successors that we do not need to now
                SiloAddress oldSuccessor = directoryPartitionsMap.FirstOrDefault(pair => !successors.Contains(pair.Key)).Key;
                if (oldSuccessor == null) return;

                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Removing copy of the directory partition of silo " + oldSuccessor + " (holding copy of " + addedSilo + " instead)");
                directoryPartitionsMap.Remove(oldSuccessor);
            }
        }

        private async Task ProcessAddedSiloAsync(SiloAddress addedSilo, List<ActivationAddress> splitPartListSingle, List<ActivationAddress> splitPartListMulti)
        {
            if (!this.localDirectory.Running) return;

            if (this.siloStatusOracle.GetApproximateSiloStatus(addedSilo) == SiloStatus.Active)
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
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Silo " + addedSilo + " is no longer active and therefore cannot receive this partition split");
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
            if (!this.localDirectory.Running) return;

            if (this.logger.IsEnabled(LogLevel.Debug))
            {
                this.logger.LogDebug(
                    $"{nameof(AcceptExistingRegistrations)}: accepting {singleActivations?.Count ?? 0} single-activation registrations and {multiActivations?.Count ?? 0} multi-activation registrations.");
            }

            if (singleActivations != null && singleActivations.Count > 0)
            {
                var tasks = singleActivations.Select(addr => this.localDirectory.RegisterAsync(addr, true, 1)).ToArray();
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
                var tasks = multiActivations.Select(addr => this.localDirectory.RegisterAsync(addr, false, 1)).ToArray();
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
        }

        internal void AcceptHandoffPartition(SiloAddress source, Dictionary<GrainId, IGrainInfo> partition, bool isFullCopy)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Got request to register " + (isFullCopy ? "FULL" : "DELTA") + " directory partition with " + partition.Count + " elements from " + source);

                if (!directoryPartitionsMap.ContainsKey(source))
                {
                    if (!isFullCopy)
                    {
                        logger.Warn(ErrorCode.DirectoryUnexpectedDelta,
                            String.Format("Got delta of the directory partition from silo {0} (Membership status {1}) while not holding a full copy. Membership active cluster size is {2}",
                                source, this.siloStatusOracle.GetApproximateSiloStatus(source),
                                this.siloStatusOracle.GetApproximateSiloStatuses(true).Count));
                    }

                    directoryPartitionsMap[source] = this.createPartion();
                }

                if (isFullCopy)
                {
                    directoryPartitionsMap[source].Set(partition);
                }
                else
                {
                    directoryPartitionsMap[source].Update(partition);
                }

                localDirectory.GsiActivationMaintainer.TrackDoubtfulGrains(partition);
            }
        }

        internal void RemoveHandoffPartition(SiloAddress source)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Got request to unregister directory partition copy from " + source);
                directoryPartitionsMap.Remove(source);
            }
        }

        private void ResetFollowers()
        {
            var copyList = silosHoldingMyPartition.ToList();
            foreach (var follower in copyList)
            {
                RemoveOldFollower(follower);
            }
        }

        private void RemoveOldFollower(SiloAddress silo)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Removing my copy from silo " + silo);
            // release this old copy, as we have got a new one
            silosHoldingMyPartition.Remove(silo);
            localDirectory.Scheduler.QueueTask(() =>
                localDirectory.GetDirectoryReference(silo).RemoveHandoffPartition(localDirectory.MyAddress),
                localDirectory.RemoteGrainDirectory.SchedulingContext).Ignore();
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
                if (this.siloStatusOracle.GetApproximateSiloStatus(pair.Key) == SiloStatus.Active)
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
                this.pendingOperations.Enqueue(new Operation(name, action));
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
                    Operation op;
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

        private struct Operation
        {
            public Operation(string name, Func<Task> action)
            {
                Name = name;
                Action = action;
            }

            public string Name { get; }

            public Func<Task> Action { get; }
        }
    }
}
