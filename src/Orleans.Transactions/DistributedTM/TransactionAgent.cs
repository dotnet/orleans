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
        private const bool selectTMByBatchSize = true;

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

        public async Task<TransactionalStatus> Commit(ITransactionInfo info)
        {
            var transactionInfo = (TransactionInfo)info;

            transactionInfo.TimeStamp = this.clock.MergeUtcNow(transactionInfo.TimeStamp);

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} prepare {transactionInfo}");

            List<ParticipantId> writeParticipants = null;
            foreach (var p in transactionInfo.Participants)
            {
                if (p.Value.Writes > 0)
                {
                    if (writeParticipants == null)
                    {
                        writeParticipants = new List<ParticipantId>();
                    }
                    writeParticipants.Add(p.Key);
                }
            }

            try
            {
                TransactionalStatus status = (writeParticipants == null)
                    ? await CommitReadOnlyTransaction(transactionInfo)
                    : await CommitReadWriteTransaction(transactionInfo, writeParticipants);
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

        private async Task<TransactionalStatus> CommitReadOnlyTransaction(TransactionInfo transactionInfo)
        {
            var participants = transactionInfo.Participants;

            var tasks = new List<Task<TransactionalStatus>>();
            foreach (var p in participants)
            {
                tasks.Add(p.Key.Reference.AsReference<ITransactionManagerExtension>()
                               .CommitReadOnly(p.Key.Name, transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp));
            }

            try
            {
                // wait for all responses
                await Task.WhenAll(tasks);

                // examine the return status
                foreach (var s in tasks)
                {
                    var status = s.Result;
                    if (status != TransactionalStatus.Ok)
                    {
                        if (logger.IsEnabled(LogLevel.Debug))
                            logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} fail {transactionInfo.TransactionId} prepare response status={status}");

                        foreach (var p in participants)
                            p.Key.Reference.AsReference<ITransactionalResourceExtension>()
                                 .Abort(p.Key.Name, transactionInfo.TransactionId)
                                 .Ignore();

                        return status;
                    }
                }
            }
            catch (TimeoutException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} timeout {transactionInfo.TransactionId} prepare responses");

                foreach(var p in participants)
                    p.Key.Reference.AsReference<ITransactionalResourceExtension>()
                         .Abort(p.Key.Name, transactionInfo.TransactionId)
                         .Ignore();

                return TransactionalStatus.ParticipantResponseTimeout;
            }

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} finish (reads only) {transactionInfo.TransactionId}");

            return TransactionalStatus.Ok;
        }

        private async Task<TransactionalStatus> CommitReadWriteTransaction(TransactionInfo transactionInfo, List<ParticipantId> writeResources)
        {
            ParticipantId tm = selectTMByBatchSize ? transactionInfo.TMCandidate : writeResources[0];
            Dictionary<ParticipantId, AccessCounter> participants = transactionInfo.Participants;

            Task<TransactionalStatus> tmPrepareAndCommitTask = null;
            foreach (var p in participants)
            {
                if (p.Key.Equals(tm))
                {
                    tmPrepareAndCommitTask = p.Key.Reference.AsReference<ITransactionManagerExtension>()
                        .PrepareAndCommit(p.Key.Name, transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp, writeResources, participants.Count);
                }
                else
                {
                    // one-way prepare message
                    p.Key.Reference.AsReference<ITransactionalResourceExtension>()
                         .Prepare(p.Key.Name, transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp, tm)
                         .Ignore();
                }
            }

            try
            {
                // wait for the TM to commit the transaction
                TransactionalStatus status = await tmPrepareAndCommitTask;

                if (status != TransactionalStatus.Ok)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} fail {transactionInfo.TransactionId} TM response status={status}");

                    // notify participants 
                    if (status.DefinitelyAborted())
                    {
                        foreach (var p in writeResources)
                        {
                            if (!p.Equals(tm))
                            {
                                // one-way cancel message
                                p.Reference.AsReference<ITransactionalResourceExtension>()
                                 .Cancel(p.Name, transactionInfo.TransactionId, transactionInfo.TimeStamp, status)
                                 .Ignore();
                            }
                        }
                    }

                    return status;
                }
            }
            catch (TimeoutException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} timeout {transactionInfo.TransactionId} TM response");

                return TransactionalStatus.TMResponseTimeout;
            }

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} finish {transactionInfo.TransactionId}");

            return TransactionalStatus.Ok;
        }

        public void Abort(ITransactionInfo info, OrleansTransactionAbortedException reason)
        {
            this.statistics.TrackTransactionFailed();
            var transactionInfo = (TransactionInfo)info;

            List<ParticipantId> participants = transactionInfo.Participants.Keys.ToList();

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"abort {transactionInfo} {string.Join(",", participants.Select(p => p.ToString()))} {reason}");

            // send one-way abort messages to release the locks and roll back any updates
            foreach (var p in participants)
            {
                p.Reference.AsReference<ITransactionalResourceExtension>()
                 .Abort(p.Name, transactionInfo.TransactionId)
                 .Ignore();
            }
        }
    }
}
