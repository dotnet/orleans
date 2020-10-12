using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Hosting;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace NetCore.Tests
{
    [Trait("Category", "BVT")]
    public class ExceptionTests : IAsyncLifetime
    {
        private ISiloHost silo;
        private IClusterClient client;

        public async Task InitializeAsync()
        {
            this.silo = new SiloHostBuilder()
                .ConfigureApplicationParts(
                    parts =>
                        parts
                        .AddApplicationPart(typeof(ExceptionGrain).Assembly)
                        .AddApplicationPart(typeof(IExceptionGrain).Assembly))
                .UseLocalhostClustering()
                .Build();
            await this.silo.StartAsync();

            this.client = new ClientBuilder()
                .ConfigureApplicationParts(parts =>
                    parts.AddApplicationPart(typeof(IExceptionGrain).Assembly))
                .UseLocalhostClustering()
                .Build();
            await this.client.Connect();
        }

        [Fact]
        public async Task ExceptionsPropagatedFromGrainToClient()
        {
            var grain = this.client.GetGrain<IExceptionGrain>(0);

            var invalidOperationException = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.ThrowsInvalidOperationException());
            Assert.Equal("Test exception", invalidOperationException.Message);

            var nullReferenceException = await Assert.ThrowsAsync<NullReferenceException>(() => grain.ThrowsNullReferenceException());
            Assert.Equal("null null null", nullReferenceException.Message);
        }

        public async Task DisposeAsync()
        {
            var cancel = new CancellationTokenSource();
            cancel.Cancel();
            if (this.silo is ISiloHost s)
            {
                await s.StopAsync(cancel.Token);
                await s.DisposeAsync();
            }

            if (this.client is IClusterClient c)
            {
                await c.AbortAsync();
                await c.DisposeAsync();
            }
        }
    }
}
