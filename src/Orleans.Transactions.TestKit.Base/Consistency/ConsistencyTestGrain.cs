using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Consistency
{
    [Reentrant]
    public class ConsistencyTestGrain : Grain, IConsistencyTestGrain
    {
        private Random random;
        private readonly ILogger logger;

        [Serializable]
        [GenerateSerializer]
        public class State
        {
            [Id(0)]
            public string WriterTx = ConsistencyTestHarness.InitialTx; // last writer
            [Id(1)]
            public int SeqNo;   // 0, 1, 2,...
        }

        protected ITransactionalState<State> data;

        public ConsistencyTestGrain(
            [TransactionalState("data", TransactionTestConstants.TransactionStore)]
            ITransactionalState<State> data,
            ILoggerFactory loggerFactory
            )
        {
            this.data = data;
            this.logger = loggerFactory.CreateLogger(nameof(ConsistencyTestGrain) + ".graincall");
        }

        private int MyNumber => (int)(this.GetPrimaryKeyLong() % ConsistencyTestOptions.MaxGrains);

        public const double recursionProbability = .1 - .9 * (1.0 / (10 * 40 - 1));

        public async Task<Observation[]> Run(ConsistencyTestOptions options, int depth, string stack, int maxgrain, DateTime stopAfter)
        {
            if (random == null)
                random = new Random(options.RandomSeed* options.NumGrains + MyNumber);

            if (depth < options.MaxDepth && random.NextDouble() < recursionProbability)
            {
                switch (random.Next(2))
                {
                    case 0:
                        return await Recurse(options, depth, stack, random, 10, ! options.AvoidDeadlocks, maxgrain, stopAfter);
                    case 1:
                        return await Recurse(options, depth, stack, random, 10, false, maxgrain, stopAfter);
                    case 2:
                        return await Recurse(options, depth, stack, random, 3, false, maxgrain, stopAfter);
                }
            }

            //if (random.Next(20 + 6 * depth) == 0)
            //{
            //    logger.LogTrace($"g{MyNumber} {data.CurrentTransactionId} {partition}.{iteration} L{depth} UserAbort");
            //    throw new UserAbort();
            //}

            var txhash = stack[..stack.IndexOf(')')].GetHashCode();

            var whethertoreadorwrite =
                  (options.ReadWrite == ReadWriteDetermination.PerTransaction) ? new Random(options.RandomSeed + txhash)
                : (options.ReadWrite == ReadWriteDetermination.PerGrain) ? new Random(options.RandomSeed + txhash * 10000 + MyNumber)
                : random;

            try
            {
                switch (whethertoreadorwrite.Next(4))
                {
                    case 0:
                        logger.LogTrace("g{MyNumber} {CurrentTransactionId} {Stack} Write", MyNumber, TransactionContext.CurrentTransactionId, stack);
                        return await Write();
                    default:
                        logger.LogTrace(
                            "g{MyNumber} {CurrentTransactionId} {stack} Read",
                            MyNumber,
                            TransactionContext.CurrentTransactionId,
                            stack);
                        return await Read();
                }
            } catch(Exception e)
            {
                logger.LogTrace("g{MyNumber} {CurrentTransactionId} {Stack} --> {ExceptionType}", MyNumber, TransactionContext.CurrentTransactionId, stack, e.GetType().Name);
                throw;
            }
        }

        private Task<Observation[]> Read()
        {
            var txid = TransactionContext.CurrentTransactionId;
            return data.PerformRead((state) =>
            {
                return new Observation[] {
                    new Observation()
                    {
                        ExecutingTx = txid,
                        WriterTx = state.WriterTx,
                        Grain = MyNumber,
                        SeqNo = state.SeqNo
                    }
                };
            });
        }

        private Task<Observation[]> Write()
        { 
            var txid = TransactionContext.CurrentTransactionId;
            return data.PerformUpdate((state) =>
            {
                var observe = new Observation[2];
                observe[0] = new Observation()
                {
                    ExecutingTx = txid,
                    WriterTx = state.WriterTx,
                    Grain = MyNumber,
                    SeqNo = state.SeqNo
                };
                state.WriterTx = txid;
                state.SeqNo++;
                observe[1] = new Observation()
                {
                    ExecutingTx = txid,
                    WriterTx = state.WriterTx,
                    Grain = MyNumber,
                    SeqNo = state.SeqNo
                };
                return observe;
            });
        }

        private async Task<Observation[]> Recurse(ConsistencyTestOptions options, int depth, string stack, Random random, int count, bool parallel, int maxgrain, DateTime stopAfter)
        {
            logger.LogTrace("g{MyNumber} {CurrentTransactionId} {Stack} Recurse {Count} {ParallelOrSequential}", MyNumber, TransactionContext.CurrentTransactionId, stack, count, (parallel ? "par" : "seq"));
            try
            {
                int min = options.AvoidDeadlocks ? MyNumber : 0;
                int max = options.AvoidDeadlocks ? maxgrain : options.NumGrains;
                var tasks = new List<Task<Observation[]>>();
                int[] targets = new int[count];
                for (int i = 0; i < count; i++)
                    targets[i] = random.Next(min, max);
                if (options.AvoidDeadlocks)
                    Array.Sort(targets);
                for (int i = 0; i < count; i++)
                {
                    var randomTarget = GrainFactory.GetGrain<IConsistencyTestGrain>(options.GrainOffset + targets[i]);
                    var maxgrainfornested = (i < count - 1) ? targets[i + 1] : max;
                    var task = randomTarget.Run(options, depth + 1, $"{stack}.{(parallel ? 'P' : 'S')}{i}", maxgrainfornested, stopAfter);
                    tasks.Add(task);
                    if (!parallel)
                        await task;
                    if (DateTime.UtcNow > stopAfter)
                        break;
                }
                await Task.WhenAll(tasks);
                var result = new HashSet<Observation>();
                for (int i = 0; i < count; i++)
                {
                    foreach (var x in tasks[i].Result)
                        result.Add(x);
                }
                return result.ToArray();
            }
            catch (Exception e)
            {
                logger.LogTrace(
                    "g{MyNumber} {CurrentTransactionId} {Stack} --> {ExceptionType}",
                    MyNumber,
                    TransactionContext.CurrentTransactionId,
                    stack,
                    e.GetType().Name);
                throw;
            }
        }
    } 
}
