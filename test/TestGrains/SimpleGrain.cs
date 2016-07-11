using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    public class SimpleGrain : Grain, ISimpleGrain 
    {
        public const string SimpleGrainNamePrefix = "UnitTests.Grains.SimpleG";

        protected Logger logger;
        protected int A { get; set; }
        protected int B { get; set; }

        public override Task OnActivateAsync()
        {
            logger = GetLogger(String.Format("{0}-{1}-{2}", typeof(SimpleGrain).Name, base.IdentityString, base.RuntimeIdentity));
            logger.Info("Activate.");
            return TaskDone.Done;
        }

        public Task SetA(int a)
        {
            logger.Info("SetA={0}", a);
            A = a;
            return TaskDone.Done;
        }

        public Task SetB(int b)
        {
            this.B = b;
            return TaskDone.Done;
        }

        public Task IncrementA()
        {
            A = A + 1;
            return TaskDone.Done;
        }

        public Task<int> GetAxB()
        {
            return Task.FromResult(A * B);
        }

        public Task<int> GetAxB(int a, int b)
        {
            return Task.FromResult(a * b);
        }

        public Task<int> GetA()
        {
            return Task.FromResult(A);
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync.");
            return TaskDone.Done;
        }
    }
}
