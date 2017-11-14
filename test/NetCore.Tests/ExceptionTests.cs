using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
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
            this.silo = SiloHostBuilder.CreateDefault().ConfigureApplicationPartManager(parts => parts.AddFromAppDomain()).ConfigureLocalHostPrimarySilo().Build();
            this.silo.StartAsync().GetAwaiter().GetResult();

            this.client = ClientBuilder.CreateDefault().ConfigureApplicationPartManager(parts => parts.AddFromAppDomain()).UseConfiguration(ClientConfiguration.LocalhostSilo()).Build();
            this.client.Connect().GetAwaiter().GetResult();
        }

        [Fact]
        public async Task ExceptionsPropagatedFromGrainToClient()
        {
            var grain = this.client.GetGrain<UnitTests.GrainInterfaces.IExceptionGrain>(0);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.ThrowsInvalidOperationException());
            Assert.Equal("Test exception", exception.Message);
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
