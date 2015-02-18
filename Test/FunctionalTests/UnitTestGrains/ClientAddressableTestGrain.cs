using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Orleans;
using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    public class ClientAddressableTestGrain : Grain, IClientAddressableTestGrain
    {
        private IClientAddressableTestClientObject target = null;

        public Task SetTarget(IClientAddressableTestClientObject target)
        {
            this.target = target;
            return TaskDone.Done;
        }

        public Task<string> HappyPath(string message)
        {
            return this.target.OnHappyPath(message);
        }

        public Task SadPath(string message)
        {
            return this.target.OnSadPath(message);
        }

        public async Task MicroSerialStressTest(int iterationCount)
        {
            for (var i = 0; i < iterationCount; ++i)
            {
                var n = await this.target.OnSerialStress(i);
                Assert.AreEqual(10000 + i, n);
            }
        }

        public Task MicroParallelStressTest(int iterationCount)
        {
            var tasks = new Task[iterationCount];
            for (var i = 0; i < iterationCount; ++i)
            {
                var n = i;
                tasks[n] = 
                    this.target.OnParallelStress(n)
                    .ContinueWith(
                        completed =>
                            {
                                Assert.IsTrue(completed.IsCompleted);
                                Assert.AreEqual(10000 + n, completed.Result);
                            });
                
            }
            return Task.WhenAll(tasks);
        }

    }
}
