using System.Collections.Generic;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Xunit;

namespace UnitTests.Runtime
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Placement")]
    [TestCategory("BVT"), TestCategory("Placement")]
    public class LocalPlacementDirectorTests
    {
        [Fact]
        public async Task PreferLocalPlacementDirector_UsesPlacementHintWhenLocalSiloIsCompatible()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var hintedSilo = Silo("127.0.0.1:101@1");
            var director = new PreferLocalPlacementDirector();
            var placementContext = CreatePlacementContext(localSilo, SiloStatus.Active, localSilo, hintedSilo);
            var target = CreateTarget(hintedSilo);

            var result = await director.OnAddActivation(strategy: null!, target, placementContext);

            Assert.Equal(hintedSilo, result);
        }

        [Fact]
        public async Task StatelessWorkerDirector_UsesPlacementHintWhenLocalSiloIsCompatible()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var hintedSilo = Silo("127.0.0.1:101@1");
            var director = new StatelessWorkerDirector();
            var placementContext = CreatePlacementContext(localSilo, SiloStatus.Active, localSilo, hintedSilo);
            var target = CreateTarget(hintedSilo);

            var result = await director.OnAddActivation(strategy: null!, target, placementContext);

            Assert.Equal(hintedSilo, result);
        }

        [Fact]
        public async Task PreferLocalPlacementDirector_FallbackQueriesCandidatesOnce()
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var remoteSilo = Silo("127.0.0.1:101@1");
            var context = CreatePlacementContext(localSilo, SiloStatus.Active, remoteSilo);
            var result = await new PreferLocalPlacementDirector().OnAddActivation(null!, CreateTarget(localSilo), context);
            Assert.Equal(remoteSilo, result);
            await context.Received(1).GetCompatibleSilosAsync(Arg.Any<PlacementTarget>(), Arg.Any<CancellationToken>());
        }

        [Theory]
        [InlineData("random")]
        [InlineData("hash")]
        [InlineData("local")]
        [InlineData("stateless")]
        public async Task PlacementDirector_AwaitsCandidatesBeforeHonoringHint(string strategy)
        {
            var localSilo = Silo("127.0.0.1:100@1");
            var remoteSilo = Silo("127.0.0.1:101@1");
            var context = CreatePlacementContext(localSilo, SiloStatus.Active, localSilo, remoteSilo);
            var completion = new TaskCompletionSource<SiloAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.GetCompatibleSilosAsync(Arg.Any<PlacementTarget>(), Arg.Any<CancellationToken>()).Returns(completion.Task);
            IPlacementDirector director = strategy switch
            {
                "random" => new RandomPlacementDirector(),
                "hash" => new HashBasedPlacementDirector(),
                "local" => new PreferLocalPlacementDirector(),
                "stateless" => new StatelessWorkerDirector(),
                _ => throw new InvalidOperationException(),
            };
            var pending = director.OnAddActivation(null!, CreateTarget(remoteSilo), context);
            Assert.False(pending.IsCompleted);
            completion.SetResult([localSilo, remoteSilo]);
            Assert.Equal(remoteSilo, await pending.WaitAsync(TestContext.Current.CancellationToken));
        }

        private static IPlacementContext CreatePlacementContext(SiloAddress localSilo, SiloStatus localSiloStatus, params SiloAddress[] compatibleSilos)
        {
            var placementContext = Substitute.For<IPlacementContext>();
            placementContext.LocalSilo.Returns(localSilo);
            placementContext.LocalSiloStatus.Returns(localSiloStatus);
            placementContext.GetCompatibleSilosAsync(Arg.Any<PlacementTarget>(), Arg.Any<CancellationToken>()).Returns(compatibleSilos);
            return placementContext;
        }

        private static PlacementTarget CreateTarget(SiloAddress placementHint) =>
            new(
                GrainId.Create("test", "grain-1"),
                new Dictionary<string, object> { [IPlacementDirector.PlacementHintKey] = placementHint },
                default,
                0);

        private static SiloAddress Silo(string value) => SiloAddress.FromParsableString(value);
    }
}
