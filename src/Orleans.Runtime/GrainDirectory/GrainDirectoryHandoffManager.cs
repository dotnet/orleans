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
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
        private const int MAX_OPERATION_DEQUEUE = 2;
        private readonly LocalGrainDirectory localDirectory;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly ILogger logger;
        private readonly Factory<GrainDirectoryPartition> createPartion;
        private readonly Queue<(string name, Func<Task> action)> pendingOperations = new Queue<(string name, Func<Task> action)>();
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
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Processing silo add event for " + addedSilo);

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
                this.pendingOperations.Enqueue((name, action));
                if (this.pendingOperations.Count <= 2)
                {
                    this.localDirectory.Scheduler.QueueTask(this.ExecutePendingOperations, this.localDirectory.RemoteGrainDirectory);
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
