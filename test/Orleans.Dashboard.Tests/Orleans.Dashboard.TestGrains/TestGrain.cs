using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace TestGrains
{
    public interface ITestGrain : IGrainWithIntegerKey
    {
        Task ExampleMethod1();

        Task ExampleMethod2();
    }

    public class TestGrain : Grain, ITestGrain, IRemindable
    {
        private readonly Random random = new();

        public async Task ExampleMethod1()
        {
            await Task.Delay(random.Next(100));
        }

        public Task ExampleMethod2()
        {
            if (random.Next(100) > 50)
            {
                throw new Exception();
            }

            return Task.CompletedTask;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await this.RegisterOrUpdateReminder("Frequent", TimeSpan.Zero, TimeSpan.FromMinutes(1));
            await this.RegisterOrUpdateReminder("Daily", TimeSpan.Zero, TimeSpan.FromDays(1));
            await this.RegisterOrUpdateReminder("Weekly", TimeSpan.Zero, TimeSpan.FromDays(7));
        }

        public Task ReceiveReminder(string reminderName, TickStatus status)
        {
            return Task.CompletedTask;
        }
    }
}