using System.Threading.Tasks;
using Orleans;

namespace TestGrains
{
    public interface ITestMessageBasedGrain : IGrainWithIntegerKey
    {
        Task<object> Receive(object message);

        Task ReceiveVoid(object message);

        Task Notify(object message);
    }

    public class TestMessageBasedGrain : Grain, ITestMessageBasedGrain
    {
        public Task<object> Receive(object message)
        {
            return Task.FromResult((object) null);
        }

        public Task ReceiveVoid(object message)
        {
            return Task.CompletedTask;
        }

        public Task Notify(object message)
        {
            return Task.CompletedTask;
        }
    }
}