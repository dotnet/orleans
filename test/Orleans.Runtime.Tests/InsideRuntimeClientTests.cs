using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester;

[TestCategory("BVT")]
public class InsideRuntimeClientTests
{
    [Fact]
    public async Task LateRejectionAfterSiloDisposalDoesNotResolveServices()
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        await using var cluster = builder.Build();
        await cluster.DeployAsync();

        var silo = cluster.Silos[0];
        var runtimeClient = silo.ServiceProvider.GetRequiredService<InsideRuntimeClient>();
        var rejection = new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Rejection,
            TargetSilo = silo.SiloAddress,
            TargetGrain = GrainId.Create("caller", Guid.NewGuid().ToString()),
            SendingGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            BodyObject = new RejectionResponse
            {
                RejectionType = Message.RejectionTypes.Unrecoverable,
                RejectionInfo = "The outbound queue is stopped",
            },
        };

        await silo.StopSiloAsync(stopGracefully: false);
        await silo.DisposeAsync();

        Assert.Null(Record.Exception(() => runtimeClient.ReceiveResponse(rejection)));
    }
}
