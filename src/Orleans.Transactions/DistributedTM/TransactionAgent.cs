using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    internal partial class TransactionAgent : ITransactionAgent
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

        public Task<TransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout)
        {
            if (overloadDetector.IsOverloaded())
            {
                this.statistics.TrackTransactionThrottled();
                throw new OrleansStartTransactionFailedException(new OrleansTransactionOverloadException());
            }

            var guid = Guid.NewGuid();
            DateTime ts = this.clock.UtcNow();

            LogTraceStartTransaction(new(stopwatch), guid, new(ts));
            this.statistics.TrackTransactionStarted();
            return Task.FromResult<TransactionInfo>(new TransactionInfo(guid, ts, ts));
        }

        public async Task<(TransactionalStatus, Exception)> Resolve(TransactionInfo transactionInfo)
        {
            transactionInfo.TimeStamp = this.clock.MergeUtcNow(transactionInfo.TimeStamp);

            LogTracePrepareTransaction(new(stopwatch), transactionInfo);
            if (transactionInfo.Participants.Count == 0)
            {
                this.statistics.TrackTransactionSucceeded();
                return (TransactionalStatus.Ok, null);
            }

            KeyValuePair<ParticipantId, AccessCounter>? manager;

            List<ParticipantId> writeParticipants;
            List<KeyValuePair<ParticipantId, AccessCounter>> resources;
            CollateParticipants(transactionInfo.Participants, out writeParticipants, out resources, out manager);
            try
            {
                var (status, exception) = (writeParticipants == null)
                    ? await CommitReadOnlyTransaction(transactionInfo, resources)
                    : await CommitReadWriteTransaction(transactionInfo, writeParticipants, resources, manager.Value);
                if (status == TransactionalStatus.Ok)
                    this.statistics.TrackTransactionSucceeded();
                else
                    this.statistics.TrackTransactionFailed();
                return (status, exception);
            }
            catch (Exception)
            {
                this.statistics.TrackTransactionFailed();
                throw;
            }
        }

        private async Task<(TransactionalStatus, Exception)> CommitReadOnlyTransaction(TransactionInfo transactionInfo, List<KeyValuePair<ParticipantId, AccessCounter>> resources)
        {
            TransactionalStatus status = TransactionalStatus.Ok;
            Exception exception;

            var tasks = new List<Task<TransactionalStatus>>();
            try
            {
                foreach (KeyValuePair<ParticipantId, AccessCounter> resource in resources)
                {
                    tasks.Add(resource.Key.Reference.AsReference<ITransactionalResourceExtension>()
                                   .CommitReadOnly(resource.Key.Name, transactionInfo.TransactionId, resource.Value, transactionInfo.TimeStamp));
                }

                // wait for all responses
                TransactionalStatus[] results = await Task.WhenAll(tasks);

                // examine the return status
                foreach (var s in results)
                {
                    if (s != TransactionalStatus.Ok)
                    {
                        status = s;
                        LogDebugPrepareTransactionFailure(new(stopwatch), transactionInfo.TransactionId, status);
                        break;
                    }
                }

                exception = null;
            }
            catch (TimeoutException ex)
            {
                LogDebugCommitReadOnlyTimeout(new(stopwatch), transactionInfo.TransactionId);
                status = TransactionalStatus.ParticipantResponseTimeout;
                exception = ex;
            }
            catch (Exception ex)
            {
                LogDebugCommitReadOnlyFailure(new(stopwatch), transactionInfo.TransactionId);
                LogWarnCommitReadOnlyFailure(transactionInfo.TransactionId, ex);
                status = TransactionalStatus.PresumedAbort;
                exception = ex;
            }

            if (status != TransactionalStatus.Ok)
            {
                try
                {
                    await Task.WhenAll(resources.Select(r => r.Key.Reference.AsReference<ITransactionalResourceExtension>()
                                .Abort(r.Key.Name, transactionInfo.TransactionId)));
                }
                catch (Exception ex)
                {
                    LogDebugCommitReadOnlyFailureAborting(new(stopwatch), transactionInfo.TransactionId, ex);
                    LogWarnFailAbortReadonlyTransaction(transactionInfo.TransactionId, ex);
                }
            }

            LogTraceFinishReadOnlyTransaction(new(stopwatch), transactionInfo.TransactionId);
            return (status, exception);
        }

        private async Task<(TransactionalStatus, Exception)> CommitReadWriteTransaction(TransactionInfo transactionInfo, List<ParticipantId> writeResources, List<KeyValuePair<ParticipantId, AccessCounter>> resources, KeyValuePair<ParticipantId, AccessCounter> manager)
        {
            TransactionalStatus status = TransactionalStatus.Ok;
            Exception exception;

            try
            {
                foreach (var p in resources)
                {
                    if (p.Key.Equals(manager.Key))
                        continue;
                    // one-way prepare message
                    p.Key.Reference.AsReference<ITransactionalResourceExtension>()
                            .Prepare(p.Key.Name, transactionInfo.TransactionId, p.Value, transactionInfo.TimeStamp, manager.Key)
                            .Ignore();
                }

                // wait for the TM to commit the transaction
                status = await manager.Key.Reference.AsReference<ITransactionManagerExtension>()
                    .PrepareAndCommit(manager.Key.Name, transactionInfo.TransactionId, manager.Value, transactionInfo.TimeStamp, writeResources, resources.Count);
                exception = null;
            }
            catch (TimeoutException ex)
            {
                LogDebugCommitReadWriteTimeout(new(stopwatch), transactionInfo.TransactionId);
                status = TransactionalStatus.TMResponseTimeout;
                exception = ex;
            }
            catch (Exception ex)
            {
                LogDebugCommitReadWriteFailure(new(stopwatch), transactionInfo.TransactionId);
                LogWarnCommitTransactionFailure(transactionInfo.TransactionId, ex);
                status = TransactionalStatus.PresumedAbort;
                exception = ex;
            }

            if (status != TransactionalStatus.Ok)
            {
                try
                {
                    LogDebugCommitTransactionFailure(new(stopwatch), transactionInfo.TransactionId, status);

                    // notify participants
                    if (status.DefinitelyAborted())
                    {
                        await Task.WhenAll(writeResources
                            .Where(p => !p.Equals(manager.Key))
                            .Select(p => p.Reference.AsReference<ITransactionalResourceExtension>()
                                    .Cancel(p.Name, transactionInfo.TransactionId, transactionInfo.TimeStamp, status)));
                    }
                }
                catch (Exception ex)
                {
                    LogDebugCommitReadWriteFailureAborting(new(stopwatch), transactionInfo.TransactionId, ex);
                    LogWarnFailAbortTransaction(transactionInfo.TransactionId, ex);
                }
            }

            LogTraceFinishTransaction(new(stopwatch), transactionInfo.TransactionId);
            return (status, exception);
        }

        public async Task Abort(TransactionInfo transactionInfo)
        {
            this.statistics.TrackTransactionFailed();

            List<ParticipantId> participants = transactionInfo.Participants.Keys.ToList();
            LogTraceAbortTransaction(transactionInfo, new(participants));

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
                if (manager == null && id.IsManager() && participant.Value.Writes > 0)
                {
                    manager = participant;
                }
            }
        }

        private readonly struct StopwatchLogRecord(Stopwatch stopwatch)
        {
            public override string ToString() => stopwatch.Elapsed.TotalMilliseconds.ToString("f2");
        }

        private readonly struct DateTimeLogRecord(DateTime ts)
        {
            public override string ToString() => ts.ToString("o");
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{TotalMilliseconds} start transaction {TransactionId} at {TimeStamp}"
        )]
        private partial void LogTraceStartTransaction(StopwatchLogRecord totalMilliseconds, Guid transactionId, DateTimeLogRecord timeStamp);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{ElapsedMilliseconds} prepare {TransactionInfo}"
        )]
        private partial void LogTracePrepareTransaction(StopwatchLogRecord elapsedMilliseconds, TransactionInfo transactionInfo);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{TotalMilliseconds} fail {TransactionId} prepare response status={Status}"
        )]
        private partial void LogDebugPrepareTransactionFailure(StopwatchLogRecord totalMilliseconds, Guid transactionId, TransactionalStatus status);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{TotalMilliseconds} timeout {TransactionId} on CommitReadOnly"
        )]
        private partial void LogDebugCommitReadOnlyTimeout(StopwatchLogRecord totalMilliseconds, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{TotalMilliseconds} failure {TransactionId} CommitReadOnly"
        )]
        private partial void LogDebugCommitReadOnlyFailure(StopwatchLogRecord totalMilliseconds, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Unknown error while commiting readonly transaction {TransactionId}"
        )]
        private partial void LogWarnCommitReadOnlyFailure(Guid transactionId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{TotalMilliseconds} failure aborting {TransactionId} CommitReadOnly"
        )]
        private partial void LogDebugCommitReadOnlyFailureAborting(StopwatchLogRecord totalMilliseconds, Guid transactionId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to abort readonly transaction {TransactionId}"
        )]
        private partial void LogWarnFailAbortReadonlyTransaction(Guid transactionId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{ElapsedMilliseconds} finish (reads only) {TransactionId}"
        )]
        private partial void LogTraceFinishReadOnlyTransaction(StopwatchLogRecord elapsedMilliseconds, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{TotalMilliseconds} timeout {TransactionId} on CommitReadWriteTransaction"
        )]
        private partial void LogDebugCommitReadWriteTimeout(StopwatchLogRecord totalMilliseconds, Guid transactionId);


        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{TotalMilliseconds} failure {TransactionId} CommitReadWriteTransaction"
        )]
        private partial void LogDebugCommitReadWriteFailure(StopwatchLogRecord totalMilliseconds, Guid transactionId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Unknown error while committing transaction {TransactionId}"
        )]
        private partial void LogWarnCommitTransactionFailure(Guid transactionId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{TotalMilliseconds} failed {TransactionId} with status={Status}"
        )]
        private partial void LogDebugCommitTransactionFailure(StopwatchLogRecord totalMilliseconds, Guid transactionId, TransactionalStatus status);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "{TotalMilliseconds} failure aborting {TransactionId} CommitReadWriteTransaction"
        )]
        private partial void LogDebugCommitReadWriteFailureAborting(StopwatchLogRecord totalMilliseconds, Guid transactionId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to abort transaction {TransactionId}"
        )]
        private partial void LogWarnFailAbortTransaction(Guid transactionId, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "{TotalMilliseconds} finish {TransactionId}"
        )]
        private partial void LogTraceFinishTransaction(StopwatchLogRecord totalMilliseconds, Guid transactionId);

        private readonly struct ParticipantsLogRecord(List<ParticipantId> participants)
        {
            public override string ToString() => string.Join(",", participants.Select(p => p.ToString()));
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Abort {TransactionInfo} {Participants}"
        )]
        private partial void LogTraceAbortTransaction(TransactionInfo transactionInfo, ParticipantsLogRecord participants);
    }
}
