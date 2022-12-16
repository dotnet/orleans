using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Storage;
using Orleans.TestingHost;
using StackExchange.Redis;
using Tester.Redis.Utility;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

namespace Tester.Redis.Persistence
{
    public partial class RedisPersistenceGrainTests
    {
        // Redis specific tests

        private GrainState state;
        private IDatabase database;

        [Fact]
        public async Task Redis_InitializeWithNoStateTest()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(0);
            var result = await grain.DoRead();

            //Assert.NotNull(result);
            Assert.Equal(default(GrainState), result);
            //Assert.Equal(default(string), result.StringValue);
            //Assert.Equal(default(int), result.IntValue);
            //Assert.Equal(default(DateTime), result.DateTimeValue);
            //Assert.Equal(default(Guid), result.GuidValue);
            //Assert.Equal(default(ITestGrain), result.GrainValue);
        }

        [Fact]
        public async Task Redis_TestStaticIdentifierGrains()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(12345);
            await grain.DoWrite(state);

            var grain2 = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(12345);
            var result = await grain2.DoRead();
            Assert.Equal(result.StringValue, state.StringValue);
            Assert.Equal(result.IntValue, state.IntValue);
            Assert.Equal(result.DateTimeValue, state.DateTimeValue);
            Assert.Equal(result.GuidValue, state.GuidValue);
            Assert.Equal(result.GrainValue, state.GrainValue);
        }

        [Fact]
        public async Task Redis_TestRedisScriptCacheClearBeforeGrainWriteState()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(1111);

            await database.ExecuteAsync("SCRIPT", "FLUSH", "SYNC");
            await grain.DoWrite(state);

            var result = await grain.DoRead();
            Assert.Equal(result.StringValue, state.StringValue);
            Assert.Equal(result.IntValue, state.IntValue);
            Assert.Equal(result.DateTimeValue, state.DateTimeValue);
            Assert.Equal(result.GuidValue, state.GuidValue);
            Assert.Equal(result.GrainValue, state.GrainValue);
        }

        [Fact]
        public async Task Redis_DoubleActivationETagConflictSimulation()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(54321);
            var data = await grain.DoRead();

            var key = grain.GetGrainId().ToString();
            await database.HashSetAsync(key, new[] { new HashEntry("etag", "derp") });

            await Assert.ThrowsAsync<InconsistentStateException>(() => grain.DoWrite(state));
        }

    }
}
