using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.Runtime.Configuration;
using DateTime = System.DateTime;

namespace Orleans.Transactions
{
    internal class TransactionAgentMetrics
    {
        //TPS in current monitor window
        private const string BatchStartTransanctionsTPS = "TransactionAgent.BatchStartTransactions.TPS";
        //avg latency in current monitor window
        private const string AvgBatchStartTransactionsLatency = "TransactionAgent.BatchStartTransactions.AvgLatency";
        private const string AvgBatchStartTransactionsBatchSize = "TransactionAgent.BatchStartTransactions.AvgBatchSize";
        private const string BatchCommitTransactionsTPS = "TransactionAgent.BatchCommitTransactions.TPS";
        private const string AvgBatchCommitTransactionsLatency = "TransactionAgent.BatchCommitTransactions.AvgLatency";
        private const string AvgBatchCommitTransactionsSize = "TransactionAgent.BatchCommitTransactions.AvgBatchSize";
        internal int BatchStartTransactionsRequestCounter { get; set; }

        internal int BatchCommitTransactionsRequestsCounter { get; set; }
        internal TimeSpan BatchStartTransactionsRequestLatencyCounter { get; set; } = TimeSpan.Zero;

        internal TimeSpan BatchCommitTransactionsRequestLatencyCounter { get; set; } = TimeSpan.Zero;
        internal int BatchStartTransactionsRequestSizeCounter { get; set; }
        internal int BatchCommitTransactionsRequestSizeCounter { get; set; }
        private DateTime lastReportTime = DateTime.UtcNow;
        private ITelemetryProducer telemetryProducer;
        private PeriodicAction periodicMonitor;

        public TransactionAgentMetrics(ITelemetryProducer producer, TimeSpan interval)
        {
            this.telemetryProducer = producer;
            this.periodicMonitor = new PeriodicAction(interval, this.ReportMetrics);
        }

        public void TryReportMetrics()
        {
            this.periodicMonitor.TryAction(DateTime.UtcNow);
        }

        private void ResetCounters(DateTime lastReportTimeStamp)
        {
            //record last report time stamp
            lastReportTime = lastReportTimeStamp;
            this.BatchStartTransactionsRequestCounter = 0;
            this.BatchCommitTransactionsRequestsCounter = 0;
            this.BatchStartTransactionsRequestLatencyCounter = TimeSpan.Zero;
            this.BatchCommitTransactionsRequestLatencyCounter = TimeSpan.Zero;
            this.BatchCommitTransactionsRequestSizeCounter = 0;
            this.BatchStartTransactionsRequestSizeCounter = 0;
        }

        private void ReportMetrics()
        {
            if (this.telemetryProducer == null)
                return;
            var now = DateTime.UtcNow;
            var timeSinceLastReportInSeconds = Math.Max(1, (now - this.lastReportTime).TotalSeconds);
            //batch start metrics
            var batchStartTransactionTPS = BatchStartTransactionsRequestCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(BatchStartTransanctionsTPS, batchStartTransactionTPS);
            if (BatchStartTransactionsRequestCounter > 0)
            {
                var avgBatchStartTransactionLatency = BatchStartTransactionsRequestLatencyCounter.Divide(BatchStartTransactionsRequestCounter);
                this.telemetryProducer.TrackMetric(AvgBatchStartTransactionsLatency,
                    avgBatchStartTransactionLatency);
                var avgBatchStartSize = BatchStartTransactionsRequestSizeCounter / BatchStartTransactionsRequestCounter;
                this.telemetryProducer.TrackMetric(AvgBatchStartTransactionsBatchSize, avgBatchStartSize);
            }

            //batch commit metrics
            var batchCommitTransactionTPS = BatchCommitTransactionsRequestsCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(BatchCommitTransactionsTPS, batchCommitTransactionTPS);
           
            if (BatchCommitTransactionsRequestsCounter > 0)
            {
                var avgBatchCommitTransactionLatency =
                BatchCommitTransactionsRequestLatencyCounter.Divide(BatchCommitTransactionsRequestsCounter);
                this.telemetryProducer.TrackMetric(AvgBatchCommitTransactionsLatency,
                    avgBatchCommitTransactionLatency);
                var avgBatchCommitSzie =
                    BatchCommitTransactionsRequestSizeCounter / BatchCommitTransactionsRequestsCounter;
                this.telemetryProducer.TrackMetric(AvgBatchCommitTransactionsSize, avgBatchCommitSzie);
            }
            
            this.ResetCounters(now);
        }
    }

    [Reentrant]
    internal class TransactionAgent : SystemTarget, ITransactionAgent, ITransactionAgentSystemTarget
    {
        private readonly ITransactionManagerService tmService;

        //private long abortSequenceNumber;
        private long abortLowerBound;
        private readonly ConcurrentDictionary<long, long> abortedTransactions;

        private readonly ConcurrentQueue<Tuple<TimeSpan, TaskCompletionSource<long>>> transactionStartQueue;
        private readonly ConcurrentQueue<TransactionInfo> transactionCommitQueue;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> commitCompletions;
        private readonly HashSet<long> outstandingCommits;

        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private IGrainTimer requestProcessor;
        private Task startTransactionsTask = Task.CompletedTask;
        private Task commitTransactionsTask = Task.CompletedTask;

        public long ReadOnlyTransactionId { get; private set; }

        //metrics related
        private TransactionAgentMetrics metrics;
        public TransactionAgent(ILocalSiloDetails siloDetails, ITransactionManagerService tmService, ILoggerFactory loggerFactory, ITelemetryProducer telemetryProducer, Factory<NodeConfiguration> getNodeConfig)
            : base(Constants.TransactionAgentSystemTargetId, siloDetails.SiloAddress, loggerFactory)
        {
            logger = loggerFactory.CreateLogger<TransactionAgent>();
            this.tmService = tmService;
            ReadOnlyTransactionId = 0;
            //abortSequenceNumber = 0;
            abortLowerBound = 0;
            this.loggerFactory = loggerFactory;

            abortedTransactions = new ConcurrentDictionary<long, long>();
            transactionStartQueue = new ConcurrentQueue<Tuple<TimeSpan, TaskCompletionSource<long>>>();
            transactionCommitQueue = new ConcurrentQueue<TransactionInfo>();
            commitCompletions = new ConcurrentDictionary<long, TaskCompletionSource<bool>>();
            outstandingCommits = new HashSet<long>();
            this.metrics = new TransactionAgentMetrics(telemetryProducer, getNodeConfig().StatisticsMetricsTableWriteInterval);
        }

        #region ITransactionAgent

        public async Task<ITransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout)
        {
            if (readOnly)
            {
                return new TransactionInfo(ReadOnlyTransactionId, true);
            }

            TransactionsStatisticsGroup.OnTransactionStartRequest();
            var completion = new TaskCompletionSource<long>();
            transactionStartQueue.Enqueue(new Tuple<TimeSpan, TaskCompletionSource<long>>(timeout, completion));

            long id = await completion.Task;
            return new TransactionInfo(id);
        }

        public async Task Commit(ITransactionInfo info)
        {
            var transactionInfo = (TransactionInfo)info;

            TransactionsStatisticsGroup.OnTransactionCommitRequest();

            if (transactionInfo.IsReadOnly)
            {
                return;
            }

            var completion = new TaskCompletionSource<bool>();
            bool canCommit = true;

            List<Task<bool>> prepareTasks = new List<Task<bool>>(transactionInfo.WriteSet.Count);
            foreach (var g in transactionInfo.WriteSet.Keys)
            {
                TransactionalResourceVersion write = TransactionalResourceVersion.Create(transactionInfo.TransactionId, transactionInfo.WriteSet[g]);
                TransactionalResourceVersion? read = null;
                if (transactionInfo.ReadSet.ContainsKey(g))
                {
                    read = transactionInfo.ReadSet[g];
                    transactionInfo.ReadSet.Remove(g);
                }
                prepareTasks.Add(g.Prepare(transactionInfo.TransactionId, write, read));
            }

            foreach (var g in transactionInfo.ReadSet.Keys)
            {
                TransactionalResourceVersion read = transactionInfo.ReadSet[g];
                prepareTasks.Add(g.Prepare(transactionInfo.TransactionId, null, read));
            }

            await Task.WhenAll(prepareTasks);
            foreach (var t in prepareTasks)
            {
                if (!t.Result)
                {
                    canCommit = false;
                }
            }

            if (!canCommit)
            {
                TransactionsStatisticsGroup.OnTransactionAborted();
                abortedTransactions.TryAdd(transactionInfo.TransactionId, 0);
                throw new OrleansPrepareFailedException(transactionInfo.TransactionId.ToString());
            }
            commitCompletions.TryAdd(transactionInfo.TransactionId, completion);
            transactionCommitQueue.Enqueue(transactionInfo);
            await completion.Task;
        }

        public void Abort(ITransactionInfo info, OrleansTransactionAbortedException reason)
        {
            var transactionInfo = (TransactionInfo)info;

            abortedTransactions.TryAdd(transactionInfo.TransactionId, 0);
            foreach (var g in transactionInfo.WriteSet.Keys)
            {
                g.Abort(transactionInfo.TransactionId).Ignore();
            }

            // TODO: should we wait for the abort tasks to complete before returning?
            // If so, how do we handle exceptions?

            // There is no guarantee that the WriteSet is complete and has all the grains.
            // Notify the TM of the abort as well.
            this.tmService.AbortTransaction(transactionInfo.TransactionId, reason).Ignore();
        }

        public bool IsAborted(long transactionId)
        {
            if (transactionId <= abortLowerBound)
            {
                return true;
            }

            return abortedTransactions.ContainsKey(transactionId) || transactionId < this.abortLowerBound;
        }

        #endregion

        private async Task ProcessRequests(object args)
        {
            // NOTE: This code is a bit complicated because we want to issue both start and commit requests,
            // but wait for each one separately in its own continuation. This can be significantly simplified
            // if we can register a separate timer for start and commit.

            List<TransactionInfo> committingTransactions = new List<TransactionInfo>();
            List<TimeSpan> startingTransactions = new List<TimeSpan>();
            List<TaskCompletionSource<long>> startCompletions = new List<TaskCompletionSource<long>>();
            
            while (transactionCommitQueue.Count > 0 || transactionStartQueue.Count > 0 || outstandingCommits.Count > 0)
            {
                this.metrics.TryReportMetrics();
                var initialAbortLowerBound = this.abortLowerBound;

                await Task.Yield();
                await WaitForWork();
                
                int startCount = transactionStartQueue.Count;
                while (startCount > 0 && startTransactionsTask.IsCompleted)
                {
                    Tuple<TimeSpan, TaskCompletionSource<long>> elem;
                    transactionStartQueue.TryDequeue(out elem);
                    startingTransactions.Add(elem.Item1);
                    startCompletions.Add(elem.Item2);

                    startCount--;
                }

                int commitCount = transactionCommitQueue.Count;
                while (commitCount > 0 && commitTransactionsTask.IsCompleted)
                {
                    TransactionInfo elem;
                    transactionCommitQueue.TryDequeue(out elem);
                    committingTransactions.Add(elem);
                    outstandingCommits.Add(elem.TransactionId);

                    commitCount--;
                }


                if (startingTransactions.Count > 0 && startTransactionsTask.IsCompleted)
                {
                    logger.Debug(ErrorCode.Transactions_SendingTMRequest, "Calling TM to start {0} transactions", startingTransactions.Count);

                    startTransactionsTask = this.StartTransactions(startingTransactions, startCompletions);
                }

                if ((committingTransactions.Count > 0 || outstandingCommits.Count > 0) && commitTransactionsTask.IsCompleted)
                {
                    logger.Debug(ErrorCode.Transactions_SendingTMRequest, "Calling TM to commit {0} transactions", committingTransactions.Count);

                    commitTransactionsTask = this.CommitTransactions(committingTransactions, outstandingCommits);

                    // Removed transactions below the abort lower bound.
                    if (this.abortLowerBound != initialAbortLowerBound)
                    {
                        foreach (var aborted in this.abortedTransactions)
                        {
                            if (aborted.Key < this.abortLowerBound)
                            {
                                long ignored;
                                this.abortedTransactions.TryRemove(aborted.Key, out ignored);
                            }
                        }
                    }
                }
            }
            this.metrics.TryReportMetrics();

        }

        private async Task CommitTransactions(List<TransactionInfo> committingTransactions,
            HashSet<long> outstandingCommits)
        {
            var stopWatch = Stopwatch.StartNew();
            try
            {
                metrics.BatchCommitTransactionsRequestsCounter++;
                metrics.BatchCommitTransactionsRequestSizeCounter += committingTransactions.Count;
                CommitTransactionsResponse commitResponse;
                try
                {
                    commitResponse = await this.tmService.CommitTransactions(committingTransactions, outstandingCommits);
                }
                finally
                {
                    stopWatch.Stop();
                    metrics.BatchCommitTransactionsRequestLatencyCounter += stopWatch.Elapsed;
                }

                var commitResults = commitResponse.CommitResult;

                // reply to clients with the outcomes we received from the TM.
                foreach (var completedId in commitResults.Keys)
                {
                    outstandingCommits.Remove(completedId);

                    TaskCompletionSource<bool> completion;
                    if (commitCompletions.TryRemove(completedId, out completion))
                    {
                        if (commitResults[completedId].Success)
                        {
                            TransactionsStatisticsGroup.OnTransactionCommitted();
                            completion.SetResult(true);
                        }
                        else
                        {
                            if (commitResults[completedId].AbortingException != null)
                            {
                                TransactionsStatisticsGroup.OnTransactionAborted();
                                completion.SetException(commitResults[completedId].AbortingException);
                            }
                            else
                            {
                                TransactionsStatisticsGroup.OnTransactionInDoubt();
                                completion.SetException(new OrleansTransactionInDoubtException(completedId.ToString()));
                            }
                        }
                    }
                }

                // Refresh cached values using new values from TM.
                this.ReadOnlyTransactionId = Math.Max(this.ReadOnlyTransactionId,
                    commitResponse.ReadOnlyTransactionId);
                this.abortLowerBound = Math.Max(this.abortLowerBound, commitResponse.AbortLowerBound);
                logger.Debug(ErrorCode.Transactions_ReceivedTMResponse,
                    "{0} transactions committed. readOnlyTransactionId {1}, abortLowerBound {2}",
                    committingTransactions.Count, ReadOnlyTransactionId, abortLowerBound);
            }
            catch (Exception e)
            {
                logger.Error(ErrorCode.Transactions_TMError, "TM Error", e);
                // Propagate the exception to every transaction in the request.
                foreach (var tx in committingTransactions)
                {
                    TransactionsStatisticsGroup.OnTransactionInDoubt();

                    TaskCompletionSource<bool> completion;
                    if (commitCompletions.TryRemove(tx.TransactionId, out completion))
                    {
                        outstandingCommits.Remove(tx.TransactionId);
                        completion.SetException(new OrleansTransactionInDoubtException(tx.TransactionId.ToString()));
                    }
                }
            }

            committingTransactions.Clear();
                
        }

        private async Task StartTransactions(List<TimeSpan> startingTransactions, List<TaskCompletionSource<long>> startCompletions)
        {
            var stopWatch = Stopwatch.StartNew();
            try
            {
                metrics.BatchStartTransactionsRequestCounter++;
                metrics.BatchStartTransactionsRequestSizeCounter += startingTransactions.Count;
                StartTransactionsResponse startResponse;
                try
                {
                    startResponse = await this.tmService.StartTransactions(startingTransactions);
                }
                finally
                {
                    stopWatch.Stop();
                    metrics.BatchStartTransactionsRequestLatencyCounter += stopWatch.Elapsed;
                }
                List<long> startedIds = startResponse.TransactionId;

                // reply to clients with results
                for (int i = 0; i < startCompletions.Count; i++)
                {
                    TransactionsStatisticsGroup.OnTransactionStarted();
                    startCompletions[i].SetResult(startedIds[i]);
                }

                // Refresh cached values using new values from TM.
                this.ReadOnlyTransactionId = Math.Max(this.ReadOnlyTransactionId, startResponse.ReadOnlyTransactionId);
                this.abortLowerBound = Math.Max(this.abortLowerBound, startResponse.AbortLowerBound);
                logger.Debug(ErrorCode.Transactions_ReceivedTMResponse,
                    "{0} Transactions started. readOnlyTransactionId {1}, abortLowerBound {2}",
                    startingTransactions.Count, ReadOnlyTransactionId, abortLowerBound);
            }
            catch (Exception e)
            {
                logger.Error(ErrorCode.Transactions_TMError, "Transaction manager failed to start transactions.", e);

                foreach (var completion in startCompletions)
                {
                    TransactionsStatisticsGroup.OnTransactionStartFailed();
                    completion.SetException(new OrleansStartTransactionFailedException(e));
                }
            }

            startingTransactions.Clear();
            startCompletions.Clear();
        }

        private Task WaitForWork()
        {
            // Returns a task that can be waited on until the RequestProcessor has
            // actionable work. The purpose is to avoid looping indefinitely while waiting
            // for the outstanding start or commit requests to complete.
            List<Task> toWait = new List<Task>();

            if (transactionStartQueue.Count > 0)
            {
                toWait.Add(startTransactionsTask);
            }

            if (transactionCommitQueue.Count > 0)
            {
                toWait.Add(commitTransactionsTask);
            }

            if (toWait.Count == 0)
            {
                return Task.CompletedTask;
            }

            return Task.WhenAny(toWait);
        }

        public Task Start()
        {
            requestProcessor = GrainTimer.FromTaskCallback(this.RuntimeClient.Scheduler, this.loggerFactory.CreateLogger<GrainTimer>(), ProcessRequests, null, TimeSpan.FromMilliseconds(0), TimeSpan.FromMilliseconds(10), "TransactionAgent");
            requestProcessor.Start();
            return Task.CompletedTask;
        }

    }
}
