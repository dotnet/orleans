using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.LeaseProviders
{
    public class AzureBlobLeaseProvider : ILeaseProvider
    {
        private CloudBlobContainer container;
        private AzureBlobLeaseProviderOptions options;
        private CloudBlobClient blobClient;
        public AzureBlobLeaseProvider(IOptions<AzureBlobLeaseProviderOptions> options)
            : this(options.Value)
        {
        }

        private AzureBlobLeaseProvider(AzureBlobLeaseProviderOptions options)
        {
            var account = CloudStorageAccount.Parse(options.DataConnectionString);
            this.blobClient = account.CreateCloudBlobClient();
            this.options = options;
        }

        private async Task InitContainerIfNotExistsAsync()
        {
            if (this.container == null)
            {
                var tmpContainer = blobClient.GetContainerReference(this.options.BlobContainerName);
                await tmpContainer.CreateIfNotExistsAsync().ConfigureAwait(false);
                this.container = tmpContainer;
            }
        }

        public async Task<AcquireLeaseResult[]> Acquire(string category, LeaseRequest[] leaseRequests)
        {
            await InitContainerIfNotExistsAsync();
            var tasks = new List<Task<AcquireLeaseResult>>(leaseRequests.Length);
            foreach (var leaseRequest in leaseRequests)
            {
                tasks.Add(Acquire(category, leaseRequest));
            }
            //Task.WhenAll will return results for each task in an array, in the same order of supplied tasks
            return await Task.WhenAll(tasks);
        }

        private string GetBlobName(string category, string resourceKey)
        {
            return $"{category.ToLower()}-{resourceKey.ToLower()}.json";
        }

        private async Task<AcquireLeaseResult> Acquire(string category, LeaseRequest leaseRequest)
        {
            try
            {
                var blob = this.container.GetBlockBlobReference(GetBlobName(category, leaseRequest.ResourceKey));
                blob.Properties.ContentType = "application/json";
                //create this blob
                await blob.UploadTextAsync("blob");
                var leaseId = await blob.AcquireLeaseAsync(leaseRequest.Duration);
                return new AcquireLeaseResult(new AcquiredLease(leaseRequest.ResourceKey, leaseRequest.Duration, leaseId, DateTime.UtcNow), ResponseCode.OK, null);
            }
            catch (StorageException e)
            {
                ResponseCode statusCode;
                //This mapping is based on references : https://docs.microsoft.com/en-us/rest/api/storageservices/blob-service-error-codes
                // https://docs.microsoft.com/en-us/rest/api/storageservices/Lease-Blob?redirectedfrom=MSDN
                switch (e.RequestInformation.HttpStatusCode)
                {
                    case 404:
                    case 409:
                    case 412: statusCode = ResponseCode.LeaseNotAvailable; break;
                    default: statusCode = ResponseCode.TransientFailure; break;
                }
                return new AcquireLeaseResult(new AcquiredLease(leaseRequest.ResourceKey), statusCode, e);
            }
        }

        public async Task Release(string category, AcquiredLease[] acquiredLeases)
        {
            await InitContainerIfNotExistsAsync();
            var tasks = new List<Task>(acquiredLeases.Length);
            foreach (var acquiredLease in acquiredLeases)
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

        public async Task<AcquireLeaseResult[]> Renew(string category, AcquiredLease[] acquiredLeases)
        {
            await InitContainerIfNotExistsAsync();
            var tasks = new List<Task<AcquireLeaseResult>>(acquiredLeases.Length);
            foreach (var acquiredLease in acquiredLeases)
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
                ResponseCode statusCode;
                //This mapping is based on references : https://docs.microsoft.com/en-us/rest/api/storageservices/blob-service-error-codes
                // https://docs.microsoft.com/en-us/rest/api/storageservices/Lease-Blob?redirectedfrom=MSDN
                switch (e.RequestInformation.HttpStatusCode)
                {
                    case 404:
                    case 409:
                    case 412: statusCode = ResponseCode.InvalidToken; break;
                    default: statusCode = ResponseCode.TransientFailure; break;
                }
                return new AcquireLeaseResult(new AcquiredLease(acquiredLease.ResourceKey), statusCode, e);
            }
        }

        public static ILeaseProvider Create(IServiceProvider services, string name)
        {
            AzureBlobLeaseProviderOptions options = services.GetOptionsByName<AzureBlobLeaseProviderOptions>(name);
            return new AzureBlobLeaseProvider(options);
        }
    }
}
