namespace StatefulCalculatorService
{
    using System;
    using System.Threading.Tasks;

    using GrainInterfaces;

    using Orleans;
    using Orleans.Providers;

    internal class TestBootstrapProvider : IBootstrapProvider
    {
        public string Name { get; private set; }

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            this.Name = name;

            Task.Factory.StartNew(async () =>
            {
                var random = new Random();
                var grain = providerRuntime.GrainFactory.GetGrain<ICalculatorGrain>(Guid.NewGuid());
                while (true)
                {
                    try
                    {
                        var value = await grain.Get();
                        ServiceEventSource.Current.Message($"{value}");
                        await grain.Add(random.NextDouble());
                        await Task.Delay(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception exception)
                    {
                        ServiceEventSource.Current.Message($"Error trying to send message to grain: {exception}");
                    }
                }
            }).Ignore();

            return Task.FromResult(0);
        }

        public Task Close() => Task.FromResult(0);
    }
}