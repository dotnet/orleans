using Orleans.Runtime;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace Orleans.Transactions.TestKit.Consistency
{
    public class ConsistencyTestHarness
    {
        private readonly ConsistencyTestOptions options;
        private Action<string> output = _ => { };

        private readonly Dictionary<int,       // Grain
                          SortedDictionary<int, // SeqNo
                          Dictionary<string,    // WriterTx
                          HashSet<string>>>>    // ReaderTx
            tuples;

        private readonly HashSet<string> succeeded;
        private readonly HashSet<string> aborted;
        private readonly Dictionary<string, string> indoubt;
        private bool timeoutsOccurred;
        private readonly bool tolerateUnknownExceptions;
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

        internal void RecordSucceeded(params Observation[] result)
        {
            if (result.Length == 0)
            {
                return;
            }

            var id = result[0].ExecutingTx;

            foreach (var tuple in result)
            {
                tuple.ExecutingTx.Should().BeEquivalentTo(id);
            }

            lock (succeeded)
            {
                succeeded.Add(id);
            }

            foreach (var tuple in result)
            {
                RecordObservation(tuple);
            }
        }

        internal void RecordObservation(Observation tuple)
        {
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

        internal void RecordAborted(string transactionId)
        {
            lock (aborted)
            {
                aborted.Add(transactionId);
            }
        }

        internal void RecordInDoubt(string transactionId, string message)
        {
            lock (indoubt)
            {
                indoubt.Add(transactionId, message);
            }
        }

        internal void RecordTimeout() => timeoutsOccurred = true;

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
                        output($"{partition}.{i} g{target} -> {result.Length} tuples");
                        RecordSucceeded(result);
                    }

                }
                catch (OrleansTransactionAbortedException e)
                {
                    output($"{partition}.{i} g{target} -> aborted {e.GetType().Name} {e.InnerException} {e.TransactionId}");
                    RecordAborted(e.TransactionId);
                }
                catch (OrleansTransactionInDoubtException f)
                {
                    output($"{partition}.{i} g{target} -> in doubt {f.TransactionId}");
                    RecordInDoubt(f.TransactionId, f.Message);
                }
                catch (System.TimeoutException)
                {
                    output($"{partition}.{i} g{target} -> timeout");
                    RecordTimeout();
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
            orderEdges.Clear();
            marks.Clear();

            foreach (var grainKvp in tuples)
            {
                CheckGrainConsistency(grainKvp.Key, grainKvp.Value);
            }

            // due a DFS to find cycles in the ordered-before graph (= violation of serializability)
            DFS();

            // report unknown exceptions
            if (!tolerateUnknownExceptions)
            {
                ReportInDoubtCommitFailures();
            }

            // report timeout exceptions
            if (!tolerateGenericTimeouts && timeoutsOccurred)
            {
                true.Should().BeFalse($"generic timeout exception caught");
            }
        }

        private void CheckGrainConsistency(
            int grain,
            SortedDictionary<int, Dictionary<string, HashSet<string>>> versions)
        {
            var pos = 0;
            var readersOfPreviousVersion = new HashSet<string>();

            foreach (var seqnoKvp in versions)
            {
                var seqno = seqnoKvp.Key;

                if (pos++ != seqno && indoubt.Count == 0 && !timeoutsOccurred)
                {
                    Fail(grain, versions, $"g{grain} is missing version v{pos - 1}, found v{seqno} instead");
                }

                var writers = seqnoKvp.Value;
                if (writers.Count != 1)
                {
                    Fail(grain, versions, $"g{grain} v{seqno} has multiple writers {string.Join(",", writers.Keys)}");
                }

                var writer = writers.First().Key;
                var readers = writers.First().Value;

                CheckWriter(grain, seqno, writer, readers, versions);
                AddPreviousVersionEdges(readersOfPreviousVersion, writer);
                AddCurrentVersionEdges(grain, seqno, writer, readers, versions);
                readersOfPreviousVersion = readers;
            }
        }

        private void CheckWriter(
            int grain,
            int seqno,
            string writer,
            HashSet<string> readers,
            SortedDictionary<int, Dictionary<string, HashSet<string>>> versions)
        {
            if (seqno == 0)
            {
                if (writer != InitialTx)
                {
                    Fail(grain, versions, $"g{grain} v{seqno} not written by {InitialTx}");
                }

                return;
            }

            if (aborted.Contains(writer))
            {
                Fail(grain, versions, $"g{grain} v{seqno} written by aborted transaction {writer}");
            }

            if (!timeoutsOccurred && !(succeeded.Contains(writer) || indoubt.ContainsKey(writer)))
            {
                Fail(grain, versions, $"g{grain} v{seqno} written by unknown transaction {writer}");
            }

            if (indoubt.Count == 0 && !timeoutsOccurred && !readers.Contains(writer))
            {
                Fail(grain, versions, $"g{grain} v{seqno} writer {writer} missing");
            }
        }

        private void AddPreviousVersionEdges(HashSet<string> readersOfPreviousVersion, string writer)
        {
            foreach (var reader in readersOfPreviousVersion)
            {
                if (reader == writer)
                {
                    continue;
                }

                if (!orderEdges.TryGetValue(reader, out var readEdges))
                {
                    orderEdges[reader] = readEdges = new HashSet<string>();
                }

                readEdges.Add(writer);
            }
        }

        private void AddCurrentVersionEdges(
            int grain,
            int seqno,
            string writer,
            HashSet<string> readers,
            SortedDictionary<int, Dictionary<string, HashSet<string>>> versions)
        {
            if (!orderEdges.TryGetValue(writer, out var writeEdges))
            {
                orderEdges[writer] = writeEdges = new HashSet<string>();
            }

            foreach (var reader in readers)
            {
                if (reader == writer)
                {
                    continue;
                }

                if (!succeeded.Contains(reader))
                {
                    Fail(grain, versions, $"g{grain} v{seqno} read by aborted transaction {reader}");
                }

                writeEdges.Add(reader);
            }
        }

        private void Fail(
            int grain,
            SortedDictionary<int, Dictionary<string, HashSet<string>>> versions,
            string message)
        {
            foreach (var version in versions)
            {
                foreach (var writer in version.Value)
                {
                    foreach (var reader in writer.Value)
                    {
                        output($"g{grain} v{version.Key} w:{writer.Key} a:{reader}");
                    }
                }
            }

            true.Should().BeFalse(message);
        }

        private void ReportInDoubtCommitFailures()
        {
            foreach (var transaction in indoubt)
            {
                if (transaction.Value.Contains("failure during transaction commit"))
                {
                    true.Should().BeFalse($"exception during commit {transaction.Key} {transaction.Value}");
                }
            }
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
