using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ConcurrentGrain : Grain, IConcurrentGrain
    {
        private ILogger logger;
        private List<IConcurrentGrain> children;
        private int index;
        private int callNumber;

        public ConcurrentGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public async Task Initialize(int ind)
        {
            this.index = ind;
            logger.LogInformation("Initialize({Index})", index);
            if (index == 0)
            {
                children = new List<IConcurrentGrain>();
                for (int i = 0; i < 1; i++)
                {
                    IConcurrentGrain grain = GrainFactory.GetGrain<IConcurrentGrain>((new Random()).Next());
                    await grain.Initialize(i + 1);
                    children.Add(grain);
                }
            }
        }

        public async Task<int> A()
        {
            callNumber++;
            int call = callNumber;
            logger.LogInformation("A() start callNumber {Call}", call);
            int i = 1;
            foreach (IConcurrentGrain child in children)
            {
                logger.LogInformation("Calling B({Index}, {Call})", i, call);
                int ret = await child.B(call);
                logger.LogInformation("Resolved the call to B({Index}, {Call})", i, call);
                i++;
            }
            logger.LogInformation("A() END callNumber {Call}", call);
            return 1;
        }

        public Task<int> B(int number)
        {
            logger.LogInformation("B({Index}) call {Number}", index, number);
            Thread.Sleep(100);
            logger.LogInformation("B({Index}) call {Number} after sleep", index, number);
            return Task.FromResult(1);
        }

        private readonly List<int> m_list = new List<int>();

        public Task<List<int>> ModifyReturnList_Test()
        {
            return Task<List<int>>.Factory.StartNew(() =>
            {
                // just do a lot of modifications of the list
                for (int i = 0; i < 10; i++)
                {
                    if (m_list.Count < 1000)
                        m_list.Add(i);
                }
                for (int i = 0; i < 5; i++)
                {
                    m_list.RemoveAt(0);
                }
                return m_list;
            });
        }

        public Task Initialize_2(int ind)
        {
            index = ind;
            logger.LogInformation("Initialize({Index})", index);
            return Task.CompletedTask;
        }

        // start a long tail call on the 1st grain by calling into the 2nd grain 
        public async Task<int> TailCall_Caller(IConcurrentReentrantGrain another, bool doCW)
        {
            logger.LogInformation("TailCall_Caller");
            if (doCW)
            {
                int i = await another.TailCall_Called();
                return i;
            }
            return await another.TailCall_Called();
        }


        // calls into the 1st grain while the tail call (TailCall_Caller) is not resolved yet.
        // if tail call optimization is working, this call should go in (the grain should be considered not executing request).
        public Task<int> TailCall_Resolver(IConcurrentReentrantGrain another)
        {
            logger.LogInformation("TailCall_Resolver");
            return another.TailCall_Resolve();
        }
    }

    [Reentrant]
    public class ConcurrentReentrantGrain : Grain, IConcurrentReentrantGrain
    {
        private ILogger logger;
        private int index;
        private TaskCompletionSource<int> resolver;

        public ConcurrentReentrantGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public Task Initialize_2(int ind)
        {
            index = ind;
            logger.LogInformation("Initialize({Index})", index);
            return Task.CompletedTask;
        }

        public Task<int> TailCall_Called()
        {
            logger.LogInformation("TailCall_Called");
            resolver = new TaskCompletionSource<int>();
            return resolver.Task;
        }

        public Task<int> TailCall_Resolve()
        {
            logger.LogInformation("TailCall_Resolve");
            resolver.SetResult(7);
            return Task.FromResult(8);
        }
    }
}
