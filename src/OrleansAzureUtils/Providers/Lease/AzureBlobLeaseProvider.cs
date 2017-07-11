using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.LeaseProviders
{
    public class AzureBlobLeaseProviderConfig
    {
        public string DataConnectionString { get; set; }
    }
    public class AzureBlobLeaseProvider : ILeaseProvider
    {
        private CloudBlobContainer container;
        private GlobalConfiguration globalConfig;
        private CloudBlobClient blobClient;
        public AzureBlobLeaseProvider(AzureBlobLeaseProviderConfig config, GlobalConfiguration globalConfig )
        {
            var account = CloudStorageAccount.Parse(config.DataConnectionString);
            this.blobClient = account.CreateCloudBlobClient();
            this.globalConfig = globalConfig;
        }

        private Task InitContainerIfNotExistsAsync()
        {
            if (this.container == null)
            {
                var containerName = $"Cluster-{globalConfig.DeploymentId}-{ResourceCategory.Streaming}-{typeof(AzureBlobLeaseProvider).Name}";
                this.container = blobClient.GetContainerReference(containerName);
                return this.container.CreateIfNotExistsAsync();
            }
            return Task.CompletedTask;
        }

        public async Task<AcquireLeaseResult[]> Acquire(string category, LeaseRequest[] leaseRequests)
        {
            await InitContainerIfNotExistsAsync();
            var tasks = new List<Task<AcquireLeaseResult>>();
            foreach (var leaseRequest in leaseRequests)
            {
                tasks.Add(Acquire(category, leaseRequest));
            }
            //Task.WhenAll will return results for each task in an array, in the same order of supplied tasks
            return await Task.WhenAll(tasks);
        }

        private ResponseCode MapHttpResponseCode(int httpResponseCode)
        {
            //This mapping is based on references : https://docs.microsoft.com/en-us/rest/api/storageservices/blob-service-error-codes
            // https://docs.microsoft.com/en-us/rest/api/storageservices/Lease-Blob?redirectedfrom=MSDN
            switch (httpResponseCode)
            {
                case 409: return ResponseCode.LeaseNotAvailable;
                case 412: return ResponseCode.InvalidToken;
                case 200:
                case 201:
                case 202: return ResponseCode.OK;
                default: return ResponseCode.TransientFailure;
            }
        }

        private string GetBlobName(string category, string resourceKey)
        {
            return $"{category}-{resourceKey}";
        }

        private async Task<AcquireLeaseResult> Acquire(string category, LeaseRequest leaseRequest)
        {
            var blob = this.container.GetBlobReference(GetBlobName(category, leaseRequest.ResourceKey));
            try
            {
                var leaseId = await blob.AcquireLeaseAsync(leaseRequest.Duration);
                return new AcquireLeaseResult(new AcquiredLease(leaseRequest.ResourceKey, leaseRequest.Duration, leaseId, DateTime.UtcNow), ResponseCode.OK, null);
            }
            catch (StorageException e)
            {
                return new AcquireLeaseResult(null, MapHttpResponseCode(e.RequestInformation.HttpStatusCode), e);
            }
        }

        public async Task Release(string category, AcquiredLease[] aquiredLeases)
        {
            await InitContainerIfNotExistsAsync();
            var tasks = new List<Task>();
            foreach (var acquiredLease in aquiredLeases)
            {
                tasks.Add(Release(category, acquiredLease));
            }
            await Task.WhenAll(tasks);
        }

        private Task Release(string category, AcquiredLease acquiredLease)
        {
            var blob = this.container.GetBlobReference(GetBlobName(category, acquiredLease.ResourceKey));
            return blob.ReleaseLeaseAsync(AccessCondition.GenerateLeaseCondition(acquiredLease.Token));
        }

        public async Task<AcquireLeaseResult[]> Renew(string category, AcquiredLease[] aquiredLeases)
        {
            await InitContainerIfNotExistsAsync();
            var tasks = new List<Task<AcquireLeaseResult>>();
            foreach (var acquiredLease in aquiredLeases)
            {
                tasks.Add(Renew(category, acquiredLease));
            }
            //Task.WhenAll will return results for each task in an array, in the same order of supplied tasks
            return await Task.WhenAll(tasks);
        }

        private async Task<AcquireLeaseResult> Renew(string category, AcquiredLease acquiredLease)
        {
            var blob = this.container.GetBlobReference(GetBlobName(category, acquiredLease.ResourceKey));

            try
            {
                await blob.RenewLeaseAsync(AccessCondition.GenerateLeaseCondition(acquiredLease.Token));
                return new AcquireLeaseResult(new AcquiredLease(acquiredLease.ResourceKey, acquiredLease.Duration, acquiredLease.Token, DateTime.UtcNow),
                    ResponseCode.OK, null);
            }
            catch (StorageException e)
            {
                return new AcquireLeaseResult(null, MapHttpResponseCode(e.RequestInformation.HttpStatusCode), e);
            }
        }
    }
}
