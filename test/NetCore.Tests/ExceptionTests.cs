using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace NetCore.Tests
{
    [Trait("Category", "BVT")]
    public class ExceptionTests : IDisposable
    {
        private readonly ISiloHost silo;
        private readonly IClusterClient client;

        public ExceptionTests()
        {
            this.silo = SiloHostBuilder.CreateDefault()
                .ConfigureApplicationParts(
                    parts =>
                        parts.AddApplicationPart(typeof(ExceptionGrain).Assembly).WithReferences())
                .ConfigureLocalHostPrimarySilo()
                .Build();
            this.silo.StartAsync().GetAwaiter().GetResult();

            this.client = ClientBuilder.CreateDefault()
                .ConfigureApplicationParts(parts =>
                    parts.AddApplicationPart(typeof(IExceptionGrain).Assembly).WithReferences())
                .UseConfiguration(ClientConfiguration.LocalhostSilo())
                .Build();
            this.client.Connect().GetAwaiter().GetResult();
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

        public void Dispose()
        {
            var cancel = new CancellationTokenSource();
            cancel.Cancel();
            this.silo?.StopAsync(cancel.Token).GetAwaiter().GetResult();
            this.silo?.Dispose();

            this.client?.Abort();
            this.client?.Dispose();
        }
    }
}
