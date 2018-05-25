using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Concurrency;
using System.Linq;
using Microsoft.Extensions.Logging;
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

        public TransactionAgent(IClock clock, ILogger<TransactionAgent> logger)
        {
            this.clock = new CausalClock(clock);
            this.logger = logger;
        }

        public Task<ITransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout)
        {
            var guid = Guid.NewGuid();
            DateTime ts = this.clock.UtcNow();

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} start transaction {guid} at {ts:o}");

            return Task.FromResult<ITransactionInfo>(new TransactionInfo(guid, ts, ts));
        }

        public async Task Commit(ITransactionInfo info)
        {
            var transactionInfo = (TransactionInfo)info;

            transactionInfo.TimeStamp = this.clock.MergeUtcNow(transactionInfo.TimeStamp);

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} prepare {transactionInfo}");

            List<ITransactionParticipant> writeParticipants = null;
            foreach (var p in transactionInfo.Participants)
            {
                if (p.Value.Writes > 0)
                {
                    if (writeParticipants == null)
                    {
                        writeParticipants = new List<ITransactionParticipant>();
                    }
                    writeParticipants.Add(p.Key);
                }
            }

            if (writeParticipants == null)
            {
                await CommitReadOnlyTransaction(transactionInfo);
            }
            else
            {
                await CommitReadWriteTransaction(transactionInfo, writeParticipants);
            }
        }

        private async Task CommitReadOnlyTransaction(TransactionInfo transactionInfo)
        {
            var participants = transactionInfo.Participants;

            var tasks = new List<Task<TransactionalStatus>>();
            foreach (var p in participants)
            {
                tasks.Add(p.Key.CommitReadOnly(transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp));
            }
            transactionInfo.PrepareMessagesSent = true;

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
                            p.Key.Abort(transactionInfo.TransactionId).Ignore();

                        throw status.ConvertToUserException(transactionInfo.TransactionId.ToString());
                    }
                }
            }
            catch (TimeoutException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} timeout {transactionInfo.TransactionId} prepare responses");

                foreach(var p in participants)
                    p.Key.Abort(transactionInfo.TransactionId).Ignore();

                throw new OrleansTransactionAbortedException(transactionInfo.TransactionId.ToString(),
                    "transaction agent timed out waiting for read-only transaction participant responses");
            }
            catch (Exception e)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} fail {transactionInfo.TransactionId} with {e.GetType().Name}");

                throw;
            }

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} finish (reads only) {transactionInfo.TransactionId}");
        }

        private async Task CommitReadWriteTransaction(TransactionInfo transactionInfo, List<ITransactionParticipant> writeParticipants)
        {
            var tm = selectTMByBatchSize ? transactionInfo.TMCandidate : writeParticipants[0];
            var participants = transactionInfo.Participants;

            Task<TransactionalStatus> tmPrepareAndCommitTask = null;
            foreach (var p in participants)
            {
                if (p.Key.Equals(tm))
                {
                    tmPrepareAndCommitTask = p.Key.PrepareAndCommit(transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp, writeParticipants, participants.Count);
                }
                else
                {
                    // one-way prepare message
                    p.Key.Prepare(transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp, tm).Ignore();
                }
            }
            transactionInfo.PrepareMessagesSent = true;

            try
            {
                // wait for the TM to commit the transaction
                var status = await tmPrepareAndCommitTask;

                if (status != TransactionalStatus.Ok)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} fail {transactionInfo.TransactionId} TM response status={status}");

                    // notify participants 
                    if (status.DefinitelyAborted())
                    {
                        foreach (var p in writeParticipants)
                        {
                            if (!p.Equals(tm))
                            {
                                // one-way cancel message
                                p.Cancel(transactionInfo.TransactionId, transactionInfo.TimeStamp, status).Ignore();
                            }
                        }
                    }

                    throw status.ConvertToUserException(transactionInfo.TransactionId.ToString());
                }
            }
            catch (TimeoutException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} timeout {transactionInfo.TransactionId} TM response");

                throw new OrleansTransactionInDoubtException(transactionInfo.TransactionId.ToString(),
                    "transaction agent timed out waiting for TM response");
            }
            catch (Exception e)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug($"{stopwatch.Elapsed.TotalMilliseconds:f2} fail {transactionInfo.TransactionId} with {e.GetType().Name}");

                throw;
            }

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"{stopwatch.Elapsed.TotalMilliseconds:f2} finish {transactionInfo.TransactionId}");
        }

        public void Abort(ITransactionInfo info, OrleansTransactionAbortedException reason)
        {
            var transactionInfo = (TransactionInfo)info;

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace($"abort {transactionInfo} {reason}");

            var participants = transactionInfo.Participants.Keys.ToList();

            if (!transactionInfo.PrepareMessagesSent)
            {
                // send one-way abort messages to release the locks and roll back any updates
                foreach (var p in participants)
                {
                    p.Abort(transactionInfo.TransactionId);
                }
            }
        }
    }
}
