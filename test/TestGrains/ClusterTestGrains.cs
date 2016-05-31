using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Core;
using Orleans.MultiCluster;
using Orleans.Runtime;
using TestGrainInterfaces;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [GlobalSingleInstance]
    public class ClusterTestGrain : Grain, IClusterTestGrain
    {
        int counter = 0;
        protected Logger logger;


        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();
            string id = this.GetPrimaryKeyLong().ToString();
            logger = GetLogger(String.Format("{0}-{1}", GetType().Name, id));
            logger.Info("Activate.");
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("Deactivate.");
            return base.OnDeactivateAsync();
        }

        public Task<int> SayHelloAsync()
        {
            counter += 1;
            return Task.FromResult(counter);
            /*
            if (name == "internal")
            {
                return reply;
            }
            else
            {
                // Talk to another grain on every odd numbered request received. Used to test internal silo-silo
                // communication.
                if (counter % 2 == 1)
                {
                    IClusterTestGrain grainRef = ClusterTestGrainFactory.GetGrain(3000);
                    string reply2 = await grainRef.SayHelloAsync("internal");
                    reply += " " + reply2;
                }
                return reply;
            }
             * */
        }
    }

    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    [GlobalSingleInstance]
    public class SimpleGlobalSingleInstanceGrain : Grain, ISimpleGlobalSingleInstanceGrain
    {
        protected Logger logger;
        protected int A { get; set; }
        protected int B { get; set; }

        public override Task OnActivateAsync()
        {
            string id = this.GetPrimaryKeyLong().ToString();
            logger = GetLogger(String.Format("{0}-{1}", GetType().Name, id));
            logger.Info("Activate.");
            return TaskDone.Done;
        }

        public Task SetA(int a)
        {
            logger.Info("SetA={0}", a);
            this.A = a;
            return TaskDone.Done;
        }

        public Task SetB(int b)
        {
            logger.Info("SetB={0}", b);
            this.B = b;
            return TaskDone.Done;
        }

        public Task<int> GetAxB()
        {
            return Task.FromResult(A * B);
        }
    }
}
