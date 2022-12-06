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
    public partial class RedisPersistenceGrainTests : GrainPersistenceTestsRunner, IClassFixture<RedisPersistenceGrainTests.Fixture>
    {
        [Fact]
        public async Task InitializeWithNoStateTest()
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
        public async Task TestStaticIdentifierGrains()
        {
            var grain = fixture.GrainFactory.GetGrain<IGrainStorageGenericGrain<GrainState>>(12345);
            GrainState state = new() {
                DateTimeValue=DateTime.UtcNow,
                GuidValue=Guid.NewGuid(),
                IntValue=12345,
                StringValue="string value",
                GrainValue=fixture.GrainFactory.GetGrain<ITestGrain>(2222)
            };
            await grain.DoWrite(state);

            var result = await grain.DoRead();
            Assert.Equal(result.StringValue, state.StringValue);
            Assert.Equal(result.IntValue, state.IntValue);
            Assert.Equal(result.DateTimeValue, state.DateTimeValue);
            Assert.Equal(result.GuidValue, state.GuidValue);
            Assert.Equal(result.GrainValue, state.GrainValue);
        }

        //[Fact]
        //public async Task TestRedisScriptCacheClearBeforeGrainWriteState()
        //{
        //    var grain = fixture.GrainFactory.GetGrain<ITestGrain>(1111);
        //    var now = DateTime.UtcNow;
        //    var guid = Guid.NewGuid();

        //    await _fixture.Database.ExecuteAsync("SCRIPT", "FLUSH", "SYNC");
        //    await grain.Set("string value", 12345, now, guid, fixture.GrainFactory.GetGrain<ITestGrain>(2222));

        //    var result = await grain.Get();
        //    Assert.Equal("string value", result.Item1);
        //    Assert.Equal(12345, result.Item2);
        //    Assert.Equal(now, result.Item3);
        //    Assert.Equal(guid, result.Item4);
        //    Assert.Equal(2222, result.Item5.GetPrimaryKeyLong());
        //}

        //[Fact]
        //public async Task Double_Activation_ETag_Conflict_Simulation()
        //{
        //    var now = DateTime.UtcNow;
        //    var guid = Guid.NewGuid();
        //    var grain = fixture.GrainFactory.GetGrain<ITestGrain>(54321);
        //    var grainId = grain.GetGrainId();

        //    var stuff = await grain.Get();
        //    var scheduler = TaskScheduler.Current;

        //    var key = grainId.ToString();
        //    await _fixture.Database.HashSetAsync(key, new[] { new HashEntry("etag", "derp") });

        //    var otherGrain = fixture.GrainFactory.GetGrain<ITestGrain>(2222);
        //    await Assert.ThrowsAsync<InconsistentStateException>(() => grain.Set("string value", 12345, now, guid, otherGrain));
        //}

    }
}
