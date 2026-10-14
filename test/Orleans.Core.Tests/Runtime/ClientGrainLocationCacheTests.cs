using Orleans;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.Runtime
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("BVT")]
    public class ClientGrainLocationCacheTests
    {
        private static readonly GrainId Grain = GrainId.Create("test", "grain");
        private static readonly SiloAddress SiloA = SiloAddress.FromParsableString("127.0.0.1:100@1");
        private static readonly SiloAddress SiloB = SiloAddress.FromParsableString("127.0.0.1:101@1");
        private static readonly SiloAddress SiloC = SiloAddress.FromParsableString("127.0.0.1:102@1");

        [Fact]
        public void CacheUpdate_CachesValidLocation()
        {
            var cache = new ClientGrainLocationCache();

            cache.OnResponse(Response(Update(SiloA, SiloB)));

            Assert.True(cache.TryGetLocation(Grain, out var location));
            Assert.Equal(SiloB, location);
        }

        [Fact]
        public void CacheUpdate_ReplacesSupersededLocation()
        {
            var cache = new ClientGrainLocationCache();
            cache.OnResponse(Response(Update(SiloC, SiloA)));

            cache.OnResponse(Response(Update(SiloA, SiloB)));

            Assert.True(cache.TryGetLocation(Grain, out var location));
            Assert.Equal(SiloB, location);
        }

        [Fact]
        public void CacheUpdate_PreservesNewerLocation()
        {
            var cache = new ClientGrainLocationCache();
            cache.OnResponse(Response(Update(SiloA, SiloB)));

            // A delayed update which supersedes an older location.
            cache.OnResponse(Response(Update(SiloC, SiloA)));

            Assert.True(cache.TryGetLocation(Grain, out var location));
            Assert.Equal(SiloB, location);
        }

        [Fact]
        public void CacheInvalidation_RemovesMatchingLocation()
        {
            var cache = new ClientGrainLocationCache();
            cache.OnResponse(Response(Update(SiloA, SiloB)));

            cache.OnResponse(Response(Update(SiloC, valid: null)));
            Assert.True(cache.TryGetLocation(Grain, out _));

            cache.OnResponse(Response(Update(SiloB, valid: null)));
            Assert.False(cache.TryGetLocation(Grain, out _));
        }

        [Fact]
        public void Rejection_RemovesRejectingSiloLocation()
        {
            var cache = new ClientGrainLocationCache();
            cache.OnResponse(Response(Update(SiloA, SiloB)));

            cache.OnResponse(Rejection(SiloA));
            Assert.True(cache.TryGetLocation(Grain, out _));

            cache.OnResponse(Rejection(SiloB));
            Assert.False(cache.TryGetLocation(Grain, out _));
        }

        [Fact]
        public void Response_WithoutCacheUpdates_PreservesLocation()
        {
            var cache = new ClientGrainLocationCache();
            cache.OnResponse(Response(Update(SiloA, SiloB)));

            using var response = new Message
            {
                Direction = Message.Directions.Response,
                Result = Message.ResponseTypes.Success,
                SendingGrain = Grain,
                SendingSilo = SiloB,
            };
            cache.OnResponse(response);

            Assert.True(cache.TryGetLocation(Grain, out var location));
            Assert.Equal(SiloB, location);
        }

        [Fact]
        public void RelocatableRequest_IsApplicationRequestToGrain()
        {
            Assert.True(Request(Grain).IsRelocatableRequest);

            var oneWay = Request(Grain);
            oneWay.Direction = Message.Directions.OneWay;
            Assert.False(oneWay.IsRelocatableRequest);

            var systemMessage = Request(Grain);
            systemMessage.IsSystemMessage = true;
            Assert.False(systemMessage.IsRelocatableRequest);

            Assert.False(Request(SystemTargetGrainId.Create(SystemTargetGrainId.CreateGrainType("test"), SiloA).GrainId).IsRelocatableRequest);
            Assert.False(Request(ClientGrainId.Create("client").GrainId).IsRelocatableRequest);
        }

        private static Message Request(GrainId target) => new()
        {
            Direction = Message.Directions.Request,
            TargetGrain = target,
        };

        private static GrainAddressCacheUpdate Update(SiloAddress invalid, SiloAddress? valid)
            => new(
                new GrainAddress { GrainId = Grain, SiloAddress = invalid },
                valid is null ? null : new GrainAddress { GrainId = Grain, SiloAddress = valid });

        private static Message Response(GrainAddressCacheUpdate update) => new()
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Success,
            SendingGrain = Grain,
            CacheInvalidationHeader = [update],
        };

        private static Message Rejection(SiloAddress rejectingSilo) => new()
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Rejection,
            SendingGrain = Grain,
            SendingSilo = rejectingSilo,
        };
    }
}
