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
    internal sealed partial class GrainDirectoryHandoffManager
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
        private const int MAX_OPERATION_DEQUEUE = 2;
        private readonly LocalGrainDirectory localDirectory;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly ILogger logger;
        private readonly Factory<LocalGrainDirectoryPartition> createPartion;
        private readonly Queue<(string name, object state, Func<GrainDirectoryHandoffManager, object, Task> action)> pendingOperations = new();
        private readonly AsyncLock executorLock = new AsyncLock();

        internal GrainDirectoryHandoffManager(
            LocalGrainDirectory localDirectory,
            ISiloStatusOracle siloStatusOracle,
            IInternalGrainFactory grainFactory,
            Factory<LocalGrainDirectoryPartition> createPartion,
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
                LogDebugProcessingSiloAddEvent(logger, addedSilo);

                // check if this is our immediate successor (i.e., if I should hold this silo's copy)
                // (if yes, adjust local and/or copied directory partitions by splitting them between old successors and the new one)
                // NOTE: We need to move part of our local directory to the new silo if it is an immediate successor.
                var successor = localDirectory.FindSuccessor(localDirectory.MyAddress);
                if (successor is null || !successor.Equals(addedSilo))
                {
                    LogDebugNotImmediateSuccessor(logger, addedSilo);
                    return;
                }

                // split my local directory and send to my new immediate successor his share
                LogDebugSplittingPartition(logger, addedSilo);
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
                    LogDebugSendingEntries(logger, splitPartListSingle.Count, addedSilo);
                }

                await localDirectory.GetDirectoryReference(addedSilo).AcceptSplitPartition(splitPartListSingle);
            }
            else
            {
                LogWarningSiloNotActive(logger, addedSilo);
                return;
            }

            if (splitPartListSingle.Count > 0)
            {
                LogDebugRemovingEntries(logger, splitPartListSingle.Count);

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

            LogDebugAcceptingRegistrations(logger, singleActivations.Count);

            var tasks = singleActivations.Select(addr => this.localDirectory.RegisterAsync(addr, previousAddress: null, 1)).ToArray();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception exception)
            {
                LogWarningExceptionRegistering(logger, exception);
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
                    LogDebugDestroyingDuplicates(logger, duplicates.Count, pair.Key, new(pair.Value));

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
                            LogWarningOperationFailedRetry(logger, exception, op.Name);
                            await Task.Delay(RetryDelay);
                        }
                        else
                        {
                            LogWarningOperationFailedNoRetry(logger, exception, op.Name);
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

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Processing silo add event for {AddedSilo}"
        )]
        private static partial void LogDebugProcessingSiloAddEvent(ILogger logger, SiloAddress addedSilo);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{AddedSilo} is not my immediate successor."
        )]
        private static partial void LogDebugNotImmediateSuccessor(ILogger logger, SiloAddress addedSilo);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Splitting my partition between me and {AddedSilo}"
        )]
        private static partial void LogDebugSplittingPartition(ILogger logger, SiloAddress addedSilo);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Sending {Count} single activation entries to {AddedSilo}"
        )]
        private static partial void LogDebugSendingEntries(ILogger logger, int count, SiloAddress addedSilo);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Silo {AddedSilo} is no longer active and therefore cannot receive this partition split"
        )]
        private static partial void LogWarningSiloNotActive(ILogger logger, SiloAddress addedSilo);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Removing {Count} single activation after partition split"
        )]
        private static partial void LogDebugRemovingEntries(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AcceptExistingRegistrations)}: accepting {{Count}} single-activation registrations"
        )]
        private static partial void LogDebugAcceptingRegistrations(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = $"Exception registering activations in {nameof(AcceptExistingRegistrations)}"
        )]
        private static partial void LogWarningExceptionRegistering(ILogger logger, Exception exception);

        private readonly struct GrainAddressesLogValue(List<GrainAddress> grainAddresses)
        {
            public override string ToString() => string.Join("\n * ", grainAddresses.Select(_ => _));
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(DestroyDuplicateActivations)} will destroy {{Count}} duplicate activations on silo {{SiloAddress}}: {{Duplicates}}"
        )]
        private static partial void LogDebugDestroyingDuplicates(ILogger logger, int count, SiloAddress siloAddress, GrainAddressesLogValue duplicates);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "{Operation} failed, will be retried"
        )]
        private static partial void LogWarningOperationFailedRetry(ILogger logger, Exception exception, string operation);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "{Operation} failed, will NOT be retried"
        )]
        private static partial void LogWarningOperationFailedNoRetry(ILogger logger, Exception exception, string operation);
    }
}
