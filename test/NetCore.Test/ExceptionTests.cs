using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Xunit;

namespace NetCore.Test
{
    [Trait("Category", "BVT")]
    public class ExceptionTests
    {
        private readonly ISiloHost silo;
        private readonly IClusterClient client;

        public ExceptionTests()
        {
            this.silo = SiloHostBuilder.CreateDefault().AddApplicationPartsFromAppDomain().ConfigureLocalHostPrimarySilo().Build();
            this.silo.StartAsync().GetAwaiter().GetResult();

            this.client = ClientBuilder.CreateDefault().AddApplicationPartsFromAppDomain().UseConfiguration(ClientConfiguration.LocalhostSilo()).Build();
            this.client.Connect().GetAwaiter().GetResult();
        }

        [Fact]
        public async Task ExceptionsPropagatedFromGrainToClient()
        {
            var grain = this.client.GetGrain<UnitTests.GrainInterfaces.IExceptionGrain>(0);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.ThrowsInvalidOperationException());
            Assert.Equal("Test exception", exception.Message);
        }
    }
}
