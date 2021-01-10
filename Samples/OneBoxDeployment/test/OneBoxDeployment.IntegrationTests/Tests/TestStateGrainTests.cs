using OneBoxDeployment.Api.Logging;
using OneBoxDeployment.IntegrationTests.HttpClients;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using Xunit.Extensions.Ordering;
using OneBoxDeployment.Common;
using System.Net.Http.Json;

namespace OneBoxDeployment.IntegrationTests.Tests
{
    /// <summary>
    /// Tests reading and writing to test state grain in Orleans cluster.
    /// </summary>
    public sealed class TestStateGrainTests: IAssemblyFixture<IntegrationTestFixture>
    {
        /// <summary>
        /// The preconfigured client to call the route that tests Orleans state.
        /// </summary>
        private TestStateClient TestStateClient { get; }

        /// <summary>
        /// Collects the logs in real-time from the system under test.
        /// </summary>
        private InMemoryLoggerProvider InMemoryLoggerProvider { get; }


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="fixture">The fixture that holds the testing setup.</param>
        public TestStateGrainTests(IntegrationTestFixture fixture)
        {
            TestStateClient = fixture.ServicesProvider.GetService<TestStateClient>();
            InMemoryLoggerProvider = fixture.InMemoryLoggerProvider;
        }


        /// <summary>
        /// Increments the target state of two grains by a random integer number and checks the result.
        /// </summary>
        [Fact]
        public async Task IncrementTwoRandomStatesByRandomNumber()
        {
            var rand = new Random();

            //First get the base situation for later comparison. It's done with a random grain ID
            //to reduce risk to inadvert data dependencies.
            var grainId1 = rand.Next(0, 10_000);
            var grainId2 = rand.Next(10_001, 20_000);
            var grain1InitialResponse = await TestStateClient.IncrementByAsync(grainId1, 0).ConfigureAwait(false);
            var grain2InitialResponse = await TestStateClient.IncrementByAsync(grainId2, 0).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.OK, grain1InitialResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, grain2InitialResponse.StatusCode);

            var grainIncrement1 = rand.Next(-100, 100);
            var grainIncrement2 = rand.Next(-100, 100);
            var grain1IncrementedResponse = await TestStateClient.IncrementByAsync(grainId1, grainIncrement1).ConfigureAwait(false);
            var grain2IncrementedResponse = await TestStateClient.IncrementByAsync(grainId2, grainIncrement2).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.OK, grain1IncrementedResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, grain2IncrementedResponse.StatusCode);

            var initialState1 = await grain1InitialResponse.Content.ReadFromJsonAsync<int>().ConfigureAwait(false);
            var initialState2 = await grain2InitialResponse.Content.ReadFromJsonAsync<int>().ConfigureAwait(false);
            var incrementedState1 = await grain1IncrementedResponse.Content.ReadFromJsonAsync<int>().ConfigureAwait(false);
            var incrementedState2 = await grain2IncrementedResponse.Content.ReadFromJsonAsync<int>().ConfigureAwait(false);
            Assert.Equal(initialState1 + grainIncrement1, incrementedState1);
            Assert.Equal(initialState2 + grainIncrement2, incrementedState2);

            //There were four calls to the API and it should log four events of this kind.
            var messages = InMemoryLoggerProvider.LogMessages;
            Assert.Equal(4, messages.Count(m => m.EventId == Events.TestEvent.Id));
        }
    }
}
