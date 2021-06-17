using Orleans.Runtime;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;

namespace Orleans.Transactions.TestKit.Consistency
{
    public class ConsistencyTestHarness
    {
        private readonly ConsistencyTestOptions options;
        private Action<string> output;

        private readonly Dictionary<int,       // Grain
                          SortedDictionary<int, // SeqNo
                          Dictionary<string,    // WriterTx
                          HashSet<string>>>>    // ReaderTx
            tuples;

        private readonly HashSet<string> succeeded;
        private readonly HashSet<string> aborted;
        private readonly Dictionary<string, string> indoubt;
        private bool timeoutsOccurred;
        private bool tolerateUnknownExceptions;
        private readonly IGrainFactory grainFactory;

        private readonly Dictionary<string, HashSet<string>> orderEdges = new Dictionary<string, HashSet<string>>();
        private readonly Dictionary<string, bool> marks = new Dictionary<string, bool>();


        public ConsistencyTestHarness(
            IGrainFactory grainFactory,
            int numGrains,
            int seed,
            bool avoidDeadlocks,
            bool avoidTimeouts,
            ReadWriteDetermination readWrite,
            bool tolerateUnknownExceptions)
        {
            this.grainFactory = grainFactory;

            numGrains.Should().BeLessThan(ConsistencyTestOptions.MaxGrains);
            this.options = new ConsistencyTestOptions()
            {
                AvoidDeadlocks = avoidDeadlocks,
                ReadWrite = readWrite,
                MaxDepth = 5,
                NumGrains = numGrains,
                RandomSeed = seed,
                AvoidTimeouts = avoidTimeouts,
                GrainOffset = (DateTime.UtcNow.Ticks & 0xFFFFFFFF) * ConsistencyTestOptions.MaxGrains,
            };

            this.tuples = new Dictionary<int, SortedDictionary<int, Dictionary<string, HashSet<string>>>>();
            this.succeeded = new HashSet<string>();
            this.aborted = new HashSet<string>();
            this.indoubt = new Dictionary<string, string>();

            // determine what to check for in the end
            this.tolerateUnknownExceptions = tolerateUnknownExceptions;
        }

        public const string InitialTx = "initial";

        public int NumAborted => aborted.Count;

        public async Task RunRandomTransactionSequence(int partition, int count, IGrainFactory grainFactory, Action<string> output)
        {
            this.output = output;
            var localRandom = new Random(options.RandomSeed + partition);

            for (int i = 0; i < count; i++)
            {
                var target = localRandom.Next(options.NumGrains);
                output($"({partition},{i}) g{target}");

                try
                {
                    var targetgrain = grainFactory.GetGrain<IConsistencyTestGrain>(options.GrainOffset + target);
                    var stopAfter = options.AvoidTimeouts ? DateTime.UtcNow + TimeSpan.FromSeconds(22) : DateTime.MaxValue;
                    var result = await targetgrain.Run(options, 0, $"({partition},{i})", options.NumGrains, stopAfter);

                    if (result.Length > 0)
                    {
                        var id = result[0].ExecutingTx;

                        lock (succeeded)
                            succeeded.Add(id);                           

                        output($"{partition}.{i} g{target} -> {result.Length} tuples");

                        foreach (var tuple in result)
                        {
                            tuple.ExecutingTx.ShouldBeEquivalentTo(id); // all effects of this transaction must have same id
                            lock (tuples)
                            {
                                if (!tuples.TryGetValue(tuple.Grain, out var versions))
                                {
                                    tuples.Add(tuple.Grain, versions = new SortedDictionary<int, Dictionary<string, HashSet<string>>>());
                                }
                                if (!versions.TryGetValue(tuple.SeqNo, out var writers))
                                {
                                    versions.Add(tuple.SeqNo, writers = new Dictionary<string, HashSet<string>>());
                                }
                                if (!writers.TryGetValue(tuple.WriterTx, out var readers))
                                {
                                    writers.Add(tuple.WriterTx, readers = new HashSet<string>());
                                }
                                readers.Add(tuple.ExecutingTx);
                            }
                        }
                    }

                }
                catch (OrleansTransactionAbortedException e)
                {
                    output($"{partition}.{i} g{target} -> aborted {e.GetType().Name} {e.InnerException} {e.TransactionId}");
                    lock (aborted)
                        aborted.Add(e.TransactionId);
                }
                catch (OrleansTransactionInDoubtException f)
                {
                    output($"{partition}.{i} g{target} -> in doubt {f.TransactionId}");
                    lock (indoubt)
                        indoubt.Add(f.TransactionId, f.Message);
                }
                catch (System.TimeoutException)
                {
                    output($"{partition}.{i} g{target} -> timeout");
                    timeoutsOccurred = true;
                }
                catch (OrleansException o)
                {
                    if (o.InnerException is RandomlyInjectedStorageException)
                        output($"{partition}.{i} g{target} -> injected fault");
                    else
                        throw;
                }
            }
        }

        public void CheckConsistency(bool tolerateGenericTimeouts = false, bool tolerateUnknownExceptions = false)
        {
            foreach (var grainKvp in tuples)
            {
                var pos = 0;
                Action<string> fail = (msg) =>
                {
                    foreach (var kvp1 in grainKvp.Value)
                        foreach (var kvp2 in kvp1.Value)
                            foreach (var r in kvp2.Value)
                                output($"g{grainKvp.Key} v{kvp1.Key} w:{kvp2.Key} a:{r}");
                    true.Should().BeFalse(msg);
                };

                HashSet<string> readersOfPreviousVersion = new HashSet<string>();
                
                foreach (var seqnoKvp in grainKvp.Value)
                {
                    var seqno = seqnoKvp.Key;

                    if (pos++ != seqno && indoubt.Count == 0 && !timeoutsOccurred)
                        fail($"g{grainKvp.Key} is missing version v{pos - 1}, found v{seqno} instead");

                    var writers = seqnoKvp.Value;
                    if (writers.Count != 1)
                        fail($"g{grainKvp.Key} v{seqno} has multiple writers {string.Join(",", writers.Keys)}");

                    var writer = writers.First().Key;
                    var readers = writers.First().Value;
 
                    if (seqno == 0)
                    {
                        if (writer != InitialTx)
                            fail($"g{grainKvp.Key} v{seqno} not written by {InitialTx}");
                    }
                    else
                    {
                        if (aborted.Contains(writer))
                            fail($"g{grainKvp.Key} v{seqno} written by aborted transaction {writer}");
                        if (!timeoutsOccurred && !(succeeded.Contains(writer) || indoubt.ContainsKey(writer)))
                            fail($"g{grainKvp.Key} v{seqno} written by unknown transaction {writer}");
                        if (indoubt.Count == 0 && !timeoutsOccurred && !readers.Contains(writer))
                            fail($"g{grainKvp.Key} v{seqno} writer {writer} missing");
                    }

                    // add edges from previous readers to this write
                    foreach (var r in readersOfPreviousVersion)
                        if (r != writer)
                        {
                            if (!orderEdges.TryGetValue(r, out var readedges))
                                orderEdges[r] = readedges = new HashSet<string>();
                            readedges.Add(writer);
                        }

                    if (!orderEdges.TryGetValue(writer, out var writeedges))
                        orderEdges[writer] = writeedges = new HashSet<string>();

                    foreach (var r in readers)
                        if (r != writer)
                        {
                            if (!succeeded.Contains(r))
                                fail($"g{grainKvp.Key} v{seqno} read by aborted transaction {r}");
                            writeedges.Add(r);
                        }

                    readersOfPreviousVersion = readers;
                }
            }

            // due a DFS to find cycles in the ordered-before graph (= violation of serializability)
            DFS();             

            // report unknown exceptions
            if (!tolerateUnknownExceptions)
            foreach (var kvp in indoubt)
                if (kvp.Value.Contains("failure during transaction commit"))
                    true.Should().BeFalse($"exception during commit {kvp.Key} {kvp.Value}");

            // report timeout exceptions          
            if (!tolerateGenericTimeouts && timeoutsOccurred)
                true.Should().BeFalse($"generic timeout exception caught");
        }


        private void DFS()
        {
            foreach (var kvp in orderEdges)
                if (!marks.ContainsKey(kvp.Key))
                {
                    var cycleFound = Visit(kvp.Key, kvp.Value);
                    cycleFound.Should().BeFalse($"found serializability violation");
                }
        }

        private bool Visit(string node, HashSet<string> edges)
        {
            if (marks.TryGetValue(node, out var mark))
            {
                if (mark)
                {
                    return false;
                }
                else
                {
                    output($"!!! CYCLE FOUND:");
                    output($"{node}");
                    return true;
                }
            }
            else
            {
                marks[node] = false;
                foreach (var n in edges)
                    if (orderEdges.TryGetValue(n, out var edges2))
                    {
                        if (Visit(n, edges2))
                        {
                            output($"{node}");
                            return true;
                        }
                    }
                marks[node] = true;
                return false;
            }
        }
    }
}
