using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    [Reentrant]
    internal class TransactionAgent : ITransactionAgent
    {
        private readonly ILogger logger;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private readonly CausalClock clock;
        private readonly ITransactionAgentStatistics statistics;
        private readonly ITransactionOverloadDetector overloadDetector;

        public TransactionAgent(IClock clock, ILogger<TransactionAgent> logger, ITransactionAgentStatistics statistics, ITransactionOverloadDetector overloadDetector)
        {
            this.clock = new CausalClock(clock);
            this.logger = logger;
            this.statistics = statistics;
            this.overloadDetector = overloadDetector;
        }

        public Task<ITransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout)
        {
            if (overloadDetector.IsOverloaded())
            {
                this.statistics.TrackTransactionThrottled();
                throw new OrleansStartTransactionFailedException(new OrleansTransactionOverloadException());
            }

            var guid = Guid.NewGuid();
            DateTime ts = this.clock.UtcNow();

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} start transaction {guid} at {ts:o}");
            this.statistics.TrackTransactionStarted();
            return Task.FromResult<ITransactionInfo>(new TransactionInfo(guid, ts, ts));
        }

        public async Task<TransactionalStatus> Resolve(ITransactionInfo info)
        {
            var transactionInfo = (TransactionInfo)info;

            transactionInfo.TimeStamp = this.clock.MergeUtcNow(transactionInfo.TimeStamp);

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} prepare {transactionInfo}");

            List<ParticipantId> writeParticipants = null;
            List<KeyValuePair<ParticipantId, AccessCounter>> resources = null;
            KeyValuePair<ParticipantId, AccessCounter>? manager;
            CollateParticipants(transactionInfo.Participants, out writeParticipants, out resources, out manager);
            try
            {
                TransactionalStatus status = (writeParticipants == null)
                    ? await CommitReadOnlyTransaction(transactionInfo, resources)
                    : await CommitReadWriteTransaction(transactionInfo, writeParticipants, resources, manager.Value);
                if (status == TransactionalStatus.Ok)
                    this.statistics.TrackTransactionSucceeded();
                else
                    this.statistics.TrackTransactionFailed();
                return status;
            }
            catch (Exception)
            {
                this.statistics.TrackTransactionFailed();
                throw;
            }
        }

        private async Task<TransactionalStatus> CommitReadOnlyTransaction(TransactionInfo transactionInfo, List<KeyValuePair<ParticipantId, AccessCounter>> resources)
        {
            TransactionalStatus status = TransactionalStatus.Ok;
            var tasks = new List<Task<TransactionalStatus>>();
            foreach (KeyValuePair<ParticipantId,AccessCounter> resource in resources)
            {
                tasks.Add(resource.Key.Reference.AsReference<ITransactionalResourceExtension>()
                               .CommitReadOnly(resource.Key.Name, transactionInfo.TransactionId, resource.Value, transactionInfo.TimeStamp));
            }

            try
            {
                // wait for all responses
                await Task.WhenAll(tasks);
            }
            catch (TimeoutException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} timeout {transactionInfo.TransactionId} on CommitReadOnly");
                status = TransactionalStatus.ParticipantResponseTimeout;
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} failure {transactionInfo.TransactionId} CommitReadOnly");
                this.logger.LogWarning(ex, "Unknown error while commiting readonly transaction {TransactionId}", transactionInfo.TransactionId);
                status = TransactionalStatus.PresumedAbort;
            }

            // examine the return status
            if (status == TransactionalStatus.Ok) foreach (var s in tasks)
            {
                status = s.Result;
                if (status != TransactionalStatus.Ok)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} fail {transactionInfo.TransactionId} prepare response status={status}");
                    break;
                }
            }

            try
            {
                if(status != TransactionalStatus.Ok)
                {
                    await Task.WhenAll(resources.Select(r => r.Key.Reference.AsReference<ITransactionalResourceExtension>()
                                .Abort(r.Key.Name, transactionInfo.TransactionId)));
                }
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} failure aborting {transactionInfo.TransactionId} CommitReadOnly");
                this.logger.LogWarning(ex, "Failed to abort readonly transaction {TransactionId}", transactionInfo.TransactionId);
            }

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} finish (reads only) {transactionInfo.TransactionId}");

            return status;
        }

        private async Task<TransactionalStatus> CommitReadWriteTransaction(TransactionInfo transactionInfo, List<ParticipantId> writeResources, List<KeyValuePair<ParticipantId, AccessCounter>> resources, KeyValuePair<ParticipantId, AccessCounter> manager)
        {
            TransactionalStatus status = TransactionalStatus.Ok;

            foreach (var p in resources)
            {
                if (p.Key.Equals(manager.Key))
                    continue;
                // one-way prepare message
                p.Key.Reference.AsReference<ITransactionalResourceExtension>()
                        .Prepare(p.Key.Name, transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp, manager.Key)
                        .Ignore();
            }

            try
            {
                // wait for the TM to commit the transaction
                status = await manager.Key.Reference.AsReference<ITransactionManagerExtension>()
                    .PrepareAndCommit(manager.Key.Name, transactionInfo.TransactionId, manager.Value, transactionInfo.TimeStamp, writeResources, resources.Count);
            }
            catch (TimeoutException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} timeout {transactionInfo.TransactionId} on CommitReadWriteTransaction");

                status = TransactionalStatus.TMResponseTimeout;
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} failure {transactionInfo.TransactionId} CommitReadWriteTransaction");
                this.logger.LogWarning(ex, "Unknown error while commiting transaction {TransactionId}", transactionInfo.TransactionId);
                status = TransactionalStatus.PresumedAbort;
            }

            try
            {
                if (status != TransactionalStatus.Ok)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} failed {transactionInfo.TransactionId} with status={status}");

                    // notify participants 
                    if (status.DefinitelyAborted())
                    {
                        await Task.WhenAll(writeResources
                            .Where(p => !p.Equals(manager))
                            .Select(p => p.Reference.AsReference<ITransactionalResourceExtension>()
                                    .Cancel(p.Name, transactionInfo.TransactionId, transactionInfo.TimeStamp, status)));
                    }
                }
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} failure aborting {transactionInfo.TransactionId} CommitReadWriteTransaction");
                this.logger.LogWarning(ex, "Failed to abort transaction {TransactionId}", transactionInfo.TransactionId);
            }


            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} finish {transactionInfo.TransactionId}");

            return status;
        }

        public async Task Abort(ITransactionInfo info)
        {
            this.statistics.TrackTransactionFailed();
            var transactionInfo = (TransactionInfo)info;

            List<ParticipantId> participants = transactionInfo.Participants.Keys.ToList();

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"abort {transactionInfo} {string.Join(",", participants.Select(p => p.ToString()))}");

            // send one-way abort messages to release the locks and roll back any updates
            await Task.WhenAll(participants.Select(p => p.Reference.AsReference<ITransactionalResourceExtension>()
                 .Abort(p.Name, transactionInfo.TransactionId)));
        }

        private void CollateParticipants(Dictionary<ParticipantId, AccessCounter> participants, out List<ParticipantId> writers, out List<KeyValuePair<ParticipantId, AccessCounter>> resources, out KeyValuePair<ParticipantId, AccessCounter>? manager)
        {
            writers = null;
            resources = null;
            manager = null;
            KeyValuePair<ParticipantId, AccessCounter>? priorityManager = null;
            foreach (KeyValuePair<ParticipantId, AccessCounter> participant in participants)
            {
                ParticipantId id = participant.Key;
                // priority manager
                if (id.IsPriorityManager())
                {
                    manager = priorityManager = (priorityManager == null)
                        ? participant
                        : throw new ArgumentOutOfRangeException(nameof(participants), "Only one priority transaction manager allowed in transaction");
                }
                // resource
                if(id.IsResource())
                {
                    if(resources == null)
                    {
                        resources = new List<KeyValuePair<ParticipantId, AccessCounter>>();
                    }
                    resources.Add(participant);
                    if(participant.Value.Writes > 0)
                    {
                        if (writers == null)
                        {
                            writers = new List<ParticipantId>();
                        }
                        writers.Add(id);
                    }
                }
                // manager
                if (manager == null && id.IsManager())
                {
                    manager = participant;
                }
            }
        }
    }
}
