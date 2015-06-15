using Orleans;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
namespace UnitTests.Grains
{
    class SimpleGrainWithMultiInheritanceNoParentMethods : Grain, ISimpleGrainWithMultiInheritanceNoParentMehods
    {
    }

    class SimpleGrainWithMultiInheritance : Grain, ISimpleGrainWithMultiInheritance
    {
        protected Logger logger;
        protected int A { get; set; }
        protected int B { get; set; }
        protected int C { get; set; }

        public override Task OnActivateAsync()
        {
            logger = GetLogger(String.Format("{0}-{1}-{2}", typeof(SimpleGrainWithMultiInheritance).Name, base.IdentityString, base.RuntimeIdentity));
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

        public Task SetC(int c)
        {
            this.C = c;
            return TaskDone.Done;
        }

        public Task<int> GetA()
        {
            return Task.FromResult(A);
        }

        public Task<int> GetB()
        {
            return Task.FromResult(B);
        }

        public Task<int> GetC()
        {
            return Task.FromResult(C);
        }

        public Task IncrementA()
        {
            A = A + 1;
            return TaskDone.Done;
        }

        public Task IncrementB()
        {
            B = B + 1;
            return TaskDone.Done;
        }

        public Task IncrementC()
        {
            C = C + 1;
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync.");
            return TaskDone.Done;
        }
    }
}
