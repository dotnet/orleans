using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

#nullable enable
namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Most methods of this class are synchronized since they might be called both
    /// from LocalGrainDirectory on CacheValidator.SchedulingContext and from RemoteGrainDirectory.
    /// </summary>
    internal sealed class GrainDirectoryHandoffManager
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
        private const int MAX_OPERATION_DEQUEUE = 2;
        private readonly LocalGrainDirectory localDirectory;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly ILogger logger;
        private readonly Factory<GrainDirectoryPartition> createPartion;
        private readonly Queue<(string name, object state, Func<GrainDirectoryHandoffManager, object, Task> action)> pendingOperations = new();
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
        }

        internal void ProcessSiloAddEvent(SiloAddress addedSilo)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Processing silo add event for {AddedSilo}", addedSilo);

                // check if this is our immediate successor (i.e., if I should hold this silo's copy)
                // (if yes, adjust local and/or copied directory partitions by splitting them between old successors and the new one)
                // NOTE: We need to move part of our local directory to the new silo if it is an immediate successor.
                var successor = localDirectory.FindSuccessor(localDirectory.MyAddress);
                if (successor is null || !successor.Equals(addedSilo))
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("{AddedSilo} is not my immediate successor.", addedSilo);
                    return;
                }

                // split my local directory and send to my new immediate successor his share
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Splitting my partition between me and {AddedSilo}", addedSilo);
                var splitPartListSingle = localDirectory.DirectoryPartition.Split(
                    grain =>
                    {
                        var s = localDirectory.CalculateGrainDirectoryPartition(grain);
                        return s != null && !localDirectory.MyAddress.Equals(s);
                    });

                EnqueueOperation($"{nameof(ProcessSiloAddEvent)}({addedSilo})", addedSilo,
                    (t, state) => t.ProcessAddedSiloAsync((SiloAddress)state, splitPartListSingle));
            }
        }

        private async Task ProcessAddedSiloAsync(SiloAddress addedSilo, List<GrainAddress> splitPartListSingle)
        {
            if (!this.localDirectory.Running) return;

            if (this.siloStatusOracle.GetApproximateSiloStatus(addedSilo) == SiloStatus.Active)
            {
                if (splitPartListSingle.Count > 0)
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Sending {Count} single activation entries to {AddedSilo}", splitPartListSingle.Count, addedSilo);
                }

                await localDirectory.GetDirectoryReference(addedSilo).AcceptSplitPartition(splitPartListSingle);
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Warning)) logger.LogWarning("Silo " + addedSilo + " is no longer active and therefore cannot receive this partition split");
                return;
            }

            if (splitPartListSingle.Count > 0)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Removing {Count} single activation after partition split", splitPartListSingle.Count);

                foreach (var activationAddress in splitPartListSingle)
                {
                    localDirectory.DirectoryPartition.RemoveGrain(activationAddress.GrainId);
                }
            }
        }

        internal void AcceptExistingRegistrations(List<GrainAddress> singleActivations)
        {
            if (singleActivations.Count == 0) return;
            EnqueueOperation(nameof(AcceptExistingRegistrations), singleActivations,
                (t, state) => t.AcceptExistingRegistrationsAsync((List<GrainAddress>)state));
        }

        private async Task AcceptExistingRegistrationsAsync(List<GrainAddress> singleActivations)
        {
            if (!this.localDirectory.Running) return;

            if (this.logger.IsEnabled(LogLevel.Debug))
            {
                this.logger.LogDebug($"{nameof(AcceptExistingRegistrations)}: accepting {{Count}} single-activation registrations", singleActivations.Count);
            }

            var tasks = singleActivations.Select(addr => this.localDirectory.RegisterAsync(addr, previousAddress: null, 1)).ToArray();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception exception)
            {
                if (this.logger.IsEnabled(LogLevel.Warning))
                    this.logger.LogWarning(exception, $"Exception registering activations in {nameof(AcceptExistingRegistrations)}");
                throw;
            }
            finally
            {
                Dictionary<SiloAddress, List<GrainAddress>>? duplicates = null;
                for (var i = tasks.Length - 1; i >= 0; i--)
                {
                    // Retry failed tasks next time.
                    if (!tasks[i].IsCompletedSuccessfully) continue;

                    // Record the applications which lost the registration race (duplicate activations).
                    var winner = tasks[i].Result;
                    if (winner.Address is not { } winnerAddress || !winnerAddress.Equals(singleActivations[i]))
                    {
                        var duplicate = singleActivations[i];
                        (CollectionsMarshal.GetValueRefOrAddDefault(duplicates ??= new(), duplicate.SiloAddress!, out _) ??= new()).Add(duplicate);
                    }

                    // Remove tasks which completed.
                    singleActivations.RemoveAt(i);
                }

                // Destroy any duplicate activations.
                DestroyDuplicateActivations(duplicates);
            }
        }

        private void DestroyDuplicateActivations(Dictionary<SiloAddress, List<GrainAddress>>? duplicates)
        {
            if (duplicates == null || duplicates.Count == 0) return;
            EnqueueOperation(nameof(DestroyDuplicateActivations), duplicates,
                (t, state) => t.DestroyDuplicateActivationsAsync((Dictionary<SiloAddress, List<GrainAddress>>)state));
        }

        private async Task DestroyDuplicateActivationsAsync(Dictionary<SiloAddress, List<GrainAddress>> duplicates)
        {
            while (duplicates.Count > 0)
            {
                var pair = duplicates.FirstOrDefault();
                if (this.siloStatusOracle.GetApproximateSiloStatus(pair.Key) == SiloStatus.Active)
                {
                    if (this.logger.IsEnabled(LogLevel.Debug))
                    {
                        this.logger.LogDebug(
                            $"{nameof(DestroyDuplicateActivations)} will destroy {{Count}} duplicate activations on silo {{SiloAddress}}: {{Duplicates}}",
                            duplicates.Count,
                            pair.Key,
                            string.Join("\n * ", pair.Value.Select(_ => _)));
                    }

                    var remoteCatalog = this.grainFactory.GetSystemTarget<ICatalog>(Constants.CatalogType, pair.Key);
                    await remoteCatalog.DeleteActivations(pair.Value, DeactivationReasonCode.DuplicateActivation, "This grain has been activated elsewhere");
                }

                duplicates.Remove(pair.Key);
            }
        }

        private void EnqueueOperation(string name, object state, Func<GrainDirectoryHandoffManager, object, Task> action)
        {
            lock (this)
            {
                this.pendingOperations.Enqueue((name, state, action));
                if (this.pendingOperations.Count <= 2)
                {
                    this.localDirectory.RemoteGrainDirectory.WorkItemGroup.QueueTask(ExecutePendingOperations, localDirectory.RemoteGrainDirectory);
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
                    (string Name, object State, Func<GrainDirectoryHandoffManager, object, Task> Action) op;
                    lock (this)
                    {
                        if (this.pendingOperations.Count == 0) break;

                        op = this.pendingOperations.Peek();
                    }

                    dequeueCount++;

                    try
                    {
                        await op.Action(this, op.State);
                        // Success, reset the dequeue count
                        dequeueCount = 0;
                    }
                    catch (Exception exception)
                    {
                        if (dequeueCount < MAX_OPERATION_DEQUEUE)
                        {
                            if (this.logger.IsEnabled(LogLevel.Warning))
                                this.logger.LogWarning(exception, "{Operation} failed, will be retried", op.Name);
                            await Task.Delay(RetryDelay);
                        }
                        else
                        {
                            if (this.logger.IsEnabled(LogLevel.Warning))
                                this.logger.LogWarning(exception, "{Operation} failed, will NOT be retried", op.Name);
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
