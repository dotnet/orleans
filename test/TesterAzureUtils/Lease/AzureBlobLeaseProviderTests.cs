using Orleans.LeaseProviders;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Lease
{
    [TestCategory("Functional"), TestCategory("Azure"), TestCategory("Lease")]
    public class AzureBlobLeaseProviderTests : AzureStorageBasicTests
    {
        private const string LeaseCategory = "AzureBlobLeaseProviderTests";
        private AzureBlobLeaseProvider leaseProvider;
        public AzureBlobLeaseProviderTests()
            :base()
        {
            var config = new AzureBlobLeaseProviderConfig()
            {
                DataConnectionString = TestDefaultConfiguration.DataConnectionString,
                BlobContainerName = "test-blob-container-name"
            };
            this.leaseProvider = new AzureBlobLeaseProvider(config);
        }

        [SkippableFact]
        public async Task ProviderCanAcquireLeases()
        {
            var leaseRequests = new List<LeaseRequest>() {
                new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15)), new LeaseRequest(Guid.NewGuid().ToString(), TimeSpan.FromSeconds(15))
            };
            //acquire
            var results = await this.leaseProvider.Acquire(LeaseCategory, leaseRequests.ToArray());
            for(int i = 0; i < results.Count(); i++)
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
                var result = renewResults[i];
                Assert.Equal(ResponseCode.InvalidToken, result.StatusCode);
                Assert.Null(result.AcquiredLease);
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

