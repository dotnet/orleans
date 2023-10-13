using Xunit;
using Xunit.Abstractions;
using Orleans.LeaseProviders;

namespace TestExtensions.Runners
{
    public class GoldenPathLeaseProviderTestRunner
    {
        private const string LeaseCategory = "TestLeaseCategory";

        private readonly ILeaseProvider leaseProvider;
        private readonly ITestOutputHelper output;

        protected GoldenPathLeaseProviderTestRunner(ILeaseProvider leaseProvider, ITestOutputHelper output)
        {
            this.leaseProvider = leaseProvider;
            this.output = output;
        }

        [SkippableFact]
        public async Task ProviderCanAcquireLeases()
        {
            var leaseRequests = new List<LeaseRequest>() {
                new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15)), new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15))
            };
            //acquire
            AcquireLeaseResult[] results = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
            for (int i = 0; i < results.Length; i++)
            {
                var result = results[i];
                Assert.Equal(ResponseCode.OK, result.StatusCode);
                Assert.NotNull(result.AcquiredLease);
                Assert.Equal(leaseRequests[i].ResourceKey, result.AcquiredLease.ResourceKey);
            };
        }

        [SkippableFact]
        public async Task ProviderCanReleaseLeases()
        {
            var leaseRequests = new List<LeaseRequest>() {
                new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15)), new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15))
            };
            //acquire
            var results = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
            //release
            await this.leaseProvider.Release(LeaseCategory, results.Select(result => result.AcquiredLease).ToArray());
        }

        [SkippableFact]
        public async Task ProviderCanRenewLeases()
        {
            var leaseRequests = new List<LeaseRequest>() {
                new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15)), new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15))
            };
            //acquire
            var acquireResults = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
            //renew
            var renewResults = await this.leaseProvider.Renew(LeaseCategory, acquireResults.Select(result => result.AcquiredLease).ToArray());
            for (int i = 0; i < renewResults.Count(); i++)
            {
                var result = renewResults[i];
                Assert.Equal(ResponseCode.OK, result.StatusCode);
                Assert.NotNull(result.AcquiredLease);
                Assert.Equal(leaseRequests[i].ResourceKey, result.AcquiredLease.ResourceKey);
            };
        }

        [SkippableFact]
        public async Task Provider_TryAcquireLeaseWhichBelongToOtherEntity_Return_LeaseNotAvailable()
        {
            var resourceId = Guid.NewGuid().ToString();
            var leaseRequests = new List<LeaseRequest>() {
                new LeaseRequest(resourceId, TimeSpan.FromSeconds(15)), new LeaseRequest(resourceId, TimeSpan.FromSeconds(15))
            };
            //two entity tries to acquire lease on the same release
            var results = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
            //one attempt succeeded and one attemp failed
            Assert.Contains(results, result => result.StatusCode == ResponseCode.OK);
            Assert.Contains(results, result => result.StatusCode == ResponseCode.LeaseNotAvailable);
        }

        [SkippableFact]
        public async Task Provider_TryRenewLeaseWithWrongToken_Return_InvalidToken()
        {
            var leaseRequests = new List<LeaseRequest>() {
                new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15)), new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15))
            };
            //acquire
            var results = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
            var acquiredLeaseWithWrongToken = results.Select(result => new AcquiredLease(result.AcquiredLease.ResourceKey, result.AcquiredLease.Duration, Guid.NewGuid().ToString(), result.AcquiredLease.StartTimeUtc));
            //renew with wrong token
            var renewResults = await this.leaseProvider.Renew(LeaseCategory, acquiredLeaseWithWrongToken.ToArray());
            for (int i = 0; i < renewResults.Count(); i++)
            {
                LeaseRequest request = leaseRequests[i];
                AcquireLeaseResult result = renewResults[i];
                Assert.Equal(ResponseCode.InvalidToken, result.StatusCode);
                Assert.Equal(request.ResourceKey, result.AcquiredLease.ResourceKey);
            }
        }

        [SkippableFact]
        public async Task Provider_LifeCycle_Acqurie_Renew_Release_ShouldBeAbleToAcquireLeaseOnTheSameResourceAfterRelease()
        {
            var resourceId = Guid.NewGuid().ToString();
            var leaseRequests = new List<LeaseRequest>() {
                new LeaseRequest(resourceId, TimeSpan.FromSeconds(15))
            };

            //acquire first time
            var acquireResults1 = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
            //renew
            var renewResults = await this.leaseProvider.Renew(LeaseCategory, acquireResults1.Select(result => result.AcquiredLease).ToArray());
            for (int i = 0; i < renewResults.Count(); i++)
            {
                var result = renewResults[i];
                Assert.Equal(ResponseCode.OK, result.StatusCode);
                Assert.NotNull(result.AcquiredLease);
                Assert.Equal(leaseRequests[i].ResourceKey, result.AcquiredLease.ResourceKey);
            }
            //release
            await this.leaseProvider.Release(LeaseCategory, renewResults.Select(result => result.AcquiredLease).ToArray());

            //acquire second time, acquire lease on the same resource after their leases got released
            var acquireResults2 = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
            for (int i = 0; i < acquireResults2.Count(); i++)
            {
                var result = acquireResults2[i];
                Assert.Equal(ResponseCode.OK, result.StatusCode);
                Assert.NotNull(result.AcquiredLease);
                Assert.Equal(leaseRequests[i].ResourceKey, result.AcquiredLease.ResourceKey);
                //token in two leases should be different
                Assert.NotEqual(acquireResults1[i].AcquiredLease.Token, result.AcquiredLease.Token);
            }
        }

    }
}
