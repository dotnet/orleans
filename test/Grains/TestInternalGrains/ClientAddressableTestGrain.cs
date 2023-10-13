using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Grains
{
    public class ClientAddressableTestGrain : Grain, IClientAddressableTestGrain
    {
        private IClientAddressableTestClientObject target;

        public Task SetTarget(IClientAddressableTestClientObject target)
        {
            this.target = target;
            return Task.CompletedTask;
        }

        public Task<string> HappyPath(string message)
        {
            return target.OnHappyPath(message);
        }

        public Task SadPath(string message)
        {
            return target.OnSadPath(message);
        }

        public async Task MicroSerialStressTest(int iterationCount)
        {
            for (var i = 0; i < iterationCount; ++i)
            {
                var n = await target.OnSerialStress(i);
                Assert.Equal(10000 + i, n);
            }
        }

        public Task MicroParallelStressTest(int iterationCount)
        {
            var tasks = new Task[iterationCount];
            for (var i = 0; i < iterationCount; ++i)
            {
                var n = i;
                tasks[n] = 
                    target.OnParallelStress(n)
                    .ContinueWith(
                        completed =>
                            {
                                Assert.True(completed.IsCompleted);
                                Assert.Equal(10000 + n, completed.Result);
                            });
                
            }
            return Task.WhenAll(tasks);
        }

    }
}
