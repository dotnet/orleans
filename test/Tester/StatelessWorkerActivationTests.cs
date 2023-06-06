using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.General;

public class StatelessWorkerActivationTests : IClassFixture<StatelessWorkerActivationTests.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Services.AddSingleton<StatelessWorkerScalingGrainSharedState>();
            }
        }
    }

    private readonly Fixture _fixture;

    public StatelessWorkerActivationTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact, TestCategory("BVT"), TestCategory("StatelessWorker")]
    public async Task SingleWorkerInvocationUnderLoad()
    {
        var workerGrain = _fixture.GrainFactory.GetGrain<IStatelessWorkerScalingGrain>(Random.Shared.Next());

        for (var i = 0; i < 100; i++)
        {
            var activationCount = await workerGrain.GetActivationCount();
            Assert.Equal(1, activationCount);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("StatelessWorker")]
    public async Task MultipleWorkerInvocationUnderLoad()
    {
        const int MaxLocalWorkers = 4;
        var waiters = new List<Task>();
        var worker = _fixture.GrainFactory.GetGrain<IStatelessWorkerScalingGrain>(1);

        var activationCount = await worker.GetActivationCount();
        Assert.Equal(1, activationCount);

        waiters.Add(worker.Wait());
        await Until(async () => 2 == await worker.GetActivationCount());
        activationCount = await worker.GetActivationCount();
        Assert.Equal(2, activationCount);

        waiters.Add(worker.Wait());
        await Until(async () => 3 == await worker.GetActivationCount());
        activationCount = await worker.GetActivationCount();
        Assert.Equal(3, activationCount);

        waiters.Add(worker.Wait());
        await Until(async () => 4 == await worker.GetActivationCount());
        activationCount = await worker.GetActivationCount();
        Assert.Equal(4, activationCount);

        var waitingCount = await worker.GetWaitingCount();
        Assert.Equal(3, waitingCount);

        for (var i = 0; i < MaxLocalWorkers; i++)
        {
            waiters.Add(worker.Wait());
        }

        await Until(async () => MaxLocalWorkers == await worker.GetActivationCount());
        await Until(async () => MaxLocalWorkers == await worker.GetWaitingCount());
        activationCount = await worker.GetActivationCount();
        Assert.Equal(MaxLocalWorkers, activationCount);
        waitingCount = await worker.GetWaitingCount();
        Assert.Equal(MaxLocalWorkers, waitingCount);

        // Release all the waiting workers.
        for (var i = 0; i < waiters.Count; i++)
        {
            await worker.Release();
        }

        // Wait for the waiting tasks to complete.
        await Task.WhenAll(waiters);
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var maxTimeout = 40_000;
        while (!await condition() && (maxTimeout -= 10) > 0) await Task.Delay(10);
        Assert.True(maxTimeout > 0);
    }
}
