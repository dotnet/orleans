using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Tester.HostBuilder.Fakes
{
    public class FakeSiloHost : ISiloHost
    {
        private TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>();

        public FakeSiloHost(IServiceProvider services)
        {
            this.Services = services;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;

        public Task StartAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            this.stopped.TrySetResult(true);
            return Task.CompletedTask;
        }

        public IServiceProvider Services { get; }

        public Task Stopped => this.stopped.Task;
    }

    internal static class FakeSiloHostBuilderExtensions
    {
        public static ISiloHostBuilder WithFakeHost(this ISiloHostBuilder builder)
        {
            builder.ConfigureServices((context, services) =>
            {

                context.Properties["OrleansServicesAdded"] = true;
                services.AddSingleton<ISiloHost, FakeSiloHost>();
            });
            return builder;
        }
    }

    public class FakeServiceProviderFactory : IServiceProviderFactory<FakeServiceCollection>
    {
        public FakeServiceCollection CreateBuilder(IServiceCollection services)
        {
            var container = new FakeServiceCollection();
            container.Populate(services);
            return container;
        }

        public IServiceProvider CreateServiceProvider(FakeServiceCollection containerBuilder)
        {
            containerBuilder.Build();
            return containerBuilder;
        }
    }

    public class FakeServiceCollection : IServiceProvider
    {
        private IServiceProvider _inner;
        private IServiceCollection _services;

        public bool FancyMethodCalled { get; private set; }

        public IServiceCollection Services => _services;

        public string State { get; set; }

        public object GetService(Type serviceType)
        {
            return _inner.GetService(serviceType);
        }

        public void Populate(IServiceCollection services)
        {
            _services = services;
            _services.AddSingleton<FakeServiceCollection>(this);
        }

        public void Build()
        {
            _inner = _services.BuildServiceProvider();
        }

        public void MyFancyContainerMethod()
        {
            FancyMethodCalled = true;
        }
    }

    public class FakeOptions { }
}
