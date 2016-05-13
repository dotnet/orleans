using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement;
using Microsoft.Practices.EnterpriseLibrary.TransientFaultHandling;
using Orleans.Runtime;
using Orleans.SqlUtils.StorageProvider.Instrumentation;

namespace Orleans.SqlUtils.StorageProvider
{
    /// <summary>
    /// The main class responsible for sending batches to a shard
    /// </summary>
    internal class ShardBatcher : IDisposable
    {
        private readonly Logger Logger;

        // Batch group is a composition of a BatcBlock and the corresponding 
        // FlushTimer and Link (to ActionBlock)
        private class BatchGroup<TEntry> : IDisposable
        {
            public BatchBlock<TEntry> BatchBlock { get; set; }
            public Timer FlushTimer { get; set; }
            public IDisposable Link { get; set; }

            public BatchGroup()
            {
            }

            public void Dispose()
            {
                if (FlushTimer != null)
                {
                    FlushTimer.Dispose();
                    FlushTimer = null;
                }

                if (Link != null)
                {
                    Link.Dispose();
                    Link = null;
                }
            }

            public void FlushBatch(object unused)
            {
                BatchBlock.TriggerBatch();
            }
        }

        private readonly Shard _shard;
        private readonly string _shardCredentials;
        private readonly ConcurrentDictionary<string, Lazy<BatchGroup<WriteEntry>>> _writeGroups;
        private readonly ConcurrentDictionary<string, Lazy<BatchGroup<ReadEntry>>> _readGroups;
        private readonly ActionBlock<IEnumerable<WriteEntry>> _writeActionBlock;
        private readonly ActionBlock<IEnumerable<ReadEntry>> _readActionBlock;
        private readonly GrainStateMap _grainStateMap;

        public RetryPolicy RetryPolicy { get; private set; }

        public int BatchSize { get; private set; }
        public int MaxConcurrentWrites { get; private set; }
        public int BatchTimeoutSeconds { get; private set; }

        internal ShardBatcher(
            Logger logger,
            GrainStateMap grainStateMap, 
            Shard shard, 
            string shardCredentials,
            BatchingOptions batchingOptions =null)
        {
            Logger = logger;

            if (null != batchingOptions)
            {
                BatchSize = batchingOptions.BatchSize;
                MaxConcurrentWrites = batchingOptions.MaxConcurrentWrites;
                BatchTimeoutSeconds = batchingOptions.BatchTimeoutSeconds;
            }

            // sanity check against bad parameters
            if (BatchSize <= 0)
                BatchSize = 1000;
            if (MaxConcurrentWrites <= 0)
                MaxConcurrentWrites = 1;
            if (BatchTimeoutSeconds <= 0)
                BatchTimeoutSeconds = 1;

            // You may want to make it configurable
            RetryPolicy = new RetryPolicy(new TransientErrorStrategy(), 5, 
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(200));
            RetryPolicy.Retrying += (sender, args) => { Logger.Warn(0, "Sql transient error", args.LastException); };

            _grainStateMap = grainStateMap;
            _shard = shard;
            _shardCredentials = shardCredentials;
            _readGroups = new ConcurrentDictionary<string, Lazy<BatchGroup<ReadEntry>>>();
            _writeGroups = new ConcurrentDictionary<string, Lazy<BatchGroup<WriteEntry>>>();
            _writeActionBlock = new ActionBlock<IEnumerable<WriteEntry>>(
                async batch => await WriteBatchDataAsync(batch),
                new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = MaxConcurrentWrites }
                );
            _readActionBlock = new ActionBlock<IEnumerable<ReadEntry>>(
                async batch => await ReadBatchDataAsync(batch),
                new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = MaxConcurrentWrites }
                );

            Logger.Info("ShardBatcher created for {0}", shard.Location.Database);
        }

        public void Dispose()
        {
            foreach (var g in _writeGroups)
                g.Value.Value.Dispose();
            foreach (var g in _readGroups)
                g.Value.Value.Dispose();

            _writeGroups.Clear();
            _readGroups.Clear();
        }

        public async Task<object> ReadStateAsync(GrainIdentity grainIdentity)
        {
            // for this specific type of grain choose the batch
            // Lazy is used to avoid side effects since valueFactory() may be called multiple times on different threads
            var readGroup = _readGroups.GetOrAdd(grainIdentity.GrainType, _ => new Lazy<BatchGroup<ReadEntry>>(() =>
            {
                var group = new BatchGroup<ReadEntry>();

                group.BatchBlock = new BatchBlock<ReadEntry>(BatchSize,
                    new GroupingDataflowBlockOptions {Greedy = true, BoundedCapacity = BatchSize*2});
                group.FlushTimer = new Timer(group.FlushBatch, null, TimeSpan.FromSeconds(BatchTimeoutSeconds),
                    TimeSpan.FromSeconds(BatchTimeoutSeconds));
                group.Link = group.BatchBlock.LinkTo(_readActionBlock);

                Logger.Info("Created ReadGroup for {0} on {1}", grainIdentity.GrainType, _shard.Location.Database);
                return group;
            })).Value;

            var tcs = new TaskCompletionSource<object>();
            // use SendAsync instead of Post to allow for buffering posted messages
            // Post would retrun false when the Block cannot accept a message
            if (await readGroup.BatchBlock.SendAsync(new ReadEntry(grainIdentity, tcs)))
            {
                InstrumentationContext.ReadPosted();
            }
            else
            {
                tcs.SetException(new ApplicationException("SendAsync did not accept the message"));
                Logger.Error("ReadStateAsync batchBlock.SendAsync did not acccept the message");
                InstrumentationContext.ReadPostFailed();
            }

            return await tcs.Task;
        }

        public async Task UpsertStateAsync(GrainIdentity grainIdentity, object state)
        {
            // for this specific type of grain choose the batch
            // Lazy is used to avoid side effects since valueFactory() may be called multiple times on different threads
            var writeGroup = _writeGroups.GetOrAdd(grainIdentity.GrainType, _ => new Lazy<BatchGroup<WriteEntry>>(() =>
            {
                var group = new BatchGroup<WriteEntry>();

                group.BatchBlock = new BatchBlock<WriteEntry>(BatchSize,
                    new GroupingDataflowBlockOptions() { Greedy = true, BoundedCapacity = BatchSize * 2 });
                group.FlushTimer = new Timer(group.FlushBatch, null, TimeSpan.FromSeconds(BatchTimeoutSeconds), TimeSpan.FromSeconds(BatchTimeoutSeconds));
                group.Link = group.BatchBlock.LinkTo(_writeActionBlock);

                Logger.Info("Created WriteGroup for {0} on {1}", grainIdentity.GrainType, _shard.Location.Database);
                return group;
            })).Value;

            TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();
            // use SendAsync instead of Post to allow for buffering posted messages
            // Post would retrun false when the Block cannot accept a message
            if (await writeGroup.BatchBlock.SendAsync(new WriteEntry(grainIdentity, state, tcs)))
            {
                InstrumentationContext.WritePosted();
            }
            else
            {
                tcs.SetException(new ApplicationException("SendAsync did not accept the message"));
                Logger.Error("UpsertStateAsync batchBlock.SendAsync did not acccept the message");
                InstrumentationContext.WritePostFailed();
            }

            await tcs.Task;
        }


        /// <summary>
        /// Callback from the Writing ActionBlock
        /// </summary>
        /// <param name="batchIn"></param>
        /// <returns></returns>
        private async Task WriteBatchDataAsync(IEnumerable<WriteEntry> batchIn)
        {
            if (!batchIn.Any())
                return;

            Logger.Info("WriteBatchDataAsync about to write to {0}", _shard.Location.Database);

            // to avoid undesired multiple enumerations of the ienumerable
            var batch = batchIn.ToList();
            try
            {
                var mapEntry = _grainStateMap.For(batch.First().GrainIdentity.GrainType);
                DataTable data = mapEntry.PrepareDataTable(batch);
                
                await RetryPolicy.ExecuteAsync(async () =>
                {
                    SqlConnection con = null;
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        // You may want to consider ConnectionOptions.Validate
                        con = await _shard.OpenConnectionAsync(_shardCredentials, ConnectionOptions.None);
                        InstrumentationContext.ConnectionOpened();

                        // Execute a simple command 
                        SqlCommand cmd = con.CreateCommand();

                        mapEntry.PrepareUpsertSqlCommand(cmd, data);

                        await cmd.ExecuteNonQueryAsync();

                        // these lines should NOT throw exceptions
                        foreach (var item in batch)
                            item.CompletionSource.SetResult(0);

                        Logger.Info("WriteBatchDataAsync count={1} elapsed: {0}", sw.Elapsed, "{0}", batch.Count);

                        InstrumentationContext.WritesCompleted(batch.Count);
                    }
                    finally 
                    {
                        if (null != con)
                        {
                            con.Close();
                            InstrumentationContext.ConnectionClosed();
                        }
                    }
                });
            }
            catch (Exception e)
            {
                foreach (var item in batch)
                    item.CompletionSource.SetException(e);
                
                Logger.Error(0, "WriteBatchDataAsync error", e);

                InstrumentationContext.WritesCompleted(batch.Count);
                InstrumentationContext.WriteErrorOccurred();

                // no exception should flow out of here, otherwise ActionBlock will transition into Faulted state
                // and stop processing the requests
            }
        }

        /// <summary>
        /// Callback from the Reading ActionBlock
        /// </summary>
        /// <param name="batchIn">Batch</param>
        /// <returns></returns>
        private async Task ReadBatchDataAsync(IEnumerable<ReadEntry> batchIn)
        {
            if (!batchIn.Any())
                return;
            
            Logger.Info("ReadBatchDataAsync about to read from {0}", _shard.Location.Database);

            // to avoid undesired multiple enumerations of the ienumerable
            var batch = batchIn.ToList();
            var completions = batch.
                    ToDictionary(entry => entry.GrainIdentity.GrainKey, entry => entry.CompletionSource);

            // this will accumulate read states
            // introduction of this local variable gives us consistency in obtaining results from SQL and handlig transient errors
            var states = new List<Tuple<string, object>>();

            try
            {
                var mapEntry = _grainStateMap.For(batch.First().GrainIdentity.GrainType);

                DataTable data = new DataTable();
                data.Columns.Add(SqlColumns.GrainKey, typeof(string));
                foreach (var entry in batch)
                    data.Rows.Add(entry.GrainIdentity.GrainKey);

                await RetryPolicy.ExecuteAsync(async () =>
                {
                    SqlConnection con = null;
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        // You may want to consider ConnectionOptions.Validate
                        con = await _shard.OpenConnectionAsync(_shardCredentials, ConnectionOptions.None);
                        InstrumentationContext.ConnectionOpened();

                        // Execute a simple command 
                        SqlCommand cmd = con.CreateCommand();
                        mapEntry.PrepareReadSqlCommand(cmd, data);
                        states = new List<Tuple<string, object>>();

                        using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                string grainKey = reader[SqlColumns.GrainKey].ToString();
                                object state = mapEntry.CreateState(reader);

                                // here we accumulate states instead of completing a given task per a state to ensure that 
                                // if subsequent reader.ReadAsync throws an exception, we stay consistent
                                states.Add(new Tuple<string, object>(grainKey, state));
                            }
                        }

                        Logger.Info("ReadBatchDataAsync count={1} elapsed: {0}", sw.Elapsed, batch.Count);

                        // finally complete all awaiting tasks to get their grain states
                        foreach (var state in states)
                        {
                            completions[state.Item1].SetResult(state.Item2); // should not throw exceptions
                            completions.Remove(state.Item1); // remove completed completion
                        }

                        // complete completions for non-existent entries
                        foreach (var c in completions)
                            c.Value.SetResult(null);

                        InstrumentationContext.ReadsCompleted(batch.Count);
                    }
                    finally
                    {
                        if (null != con)
                        {
                            con.Close();
                            InstrumentationContext.ConnectionClosed();
                        }
                    }
                });
                
            }
            catch (Exception e)
            {
                Logger.Error(0, "ReadBatchDataAsync error", e);

                // cancel all awaiting tasks to get their grain states
                foreach (var com in completions)
                    com.Value.SetException(e); // should not throw exceptions

                InstrumentationContext.ReadsCompleted(batch.Count);
                InstrumentationContext.ReadErrorOccurred();

                // no exception should flow out of here, otherwise ActionBlock will transition into Faulted state
                // and stop processing the requests
            }
        }

        private class TransientErrorStrategy : ITransientErrorDetectionStrategy
        {
            public bool IsTransient(Exception ex)
            {
                InstrumentationContext.SqlTransientErrorOccurred();
                return ex is SqlException;
            }
        }
    }
}