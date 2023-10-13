using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.LeaseProviders
{
    public class AzureBlobLeaseProvider : ILeaseProvider
    {
        private BlobContainerClient container;
        private readonly AzureBlobLeaseProviderOptions options;
        private BlobServiceClient blobClient;
        public AzureBlobLeaseProvider(IOptions<AzureBlobLeaseProviderOptions> options)
            : this(options.Value)
        {
        }

        private AzureBlobLeaseProvider(AzureBlobLeaseProviderOptions options)
        {
            this.options = options;
        }

        private async Task InitContainerIfNotExistsAsync()
        {
            if (this.container == null)
            {
                this.blobClient = await options.CreateClient();
                var tmpContainer = blobClient.GetBlobContainerClient(this.options.BlobContainerName);
                await tmpContainer.CreateIfNotExistsAsync().ConfigureAwait(false);
                this.container = tmpContainer;
            }
        }

        private BlobClient GetBlobClient(string category, string resourceKey) => this.container.GetBlobClient($"{category.ToLowerInvariant()}-{resourceKey.ToLowerInvariant()}.json");

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

        private async Task<AcquireLeaseResult> Acquire(string category, LeaseRequest leaseRequest)
        {
            try
            {
                var blobClient = GetBlobClient(category, leaseRequest.ResourceKey);
                //create this blob
                await blobClient.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("blob")), new BlobHttpHeaders { ContentType = "application/json" });
                var leaseClient = blobClient.GetBlobLeaseClient();
                var lease = await leaseClient.AcquireAsync(leaseRequest.Duration);
                return new AcquireLeaseResult(new AcquiredLease(leaseRequest.ResourceKey, leaseRequest.Duration, lease.Value.LeaseId, DateTime.UtcNow), ResponseCode.OK, null);
            }
            catch (RequestFailedException e)
            {
                ResponseCode statusCode;
                //This mapping is based on references : https://learn.microsoft.com/rest/api/storageservices/blob-service-error-codes
                // https://learn.microsoft.com/rest/api/storageservices/Lease-Blob?redirectedfrom=MSDN
                switch (e.Status)
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
            var leaseClient = GetBlobClient(category, acquiredLease.ResourceKey).GetBlobLeaseClient(acquiredLease.Token);
            return leaseClient.ReleaseAsync();
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
            var leaseClient = GetBlobClient(category, acquiredLease.ResourceKey).GetBlobLeaseClient(acquiredLease.Token);

            try
            {
                await leaseClient.RenewAsync();
                return new AcquireLeaseResult(new AcquiredLease(acquiredLease.ResourceKey, acquiredLease.Duration, acquiredLease.Token, DateTime.UtcNow),
                    ResponseCode.OK, null);
            }
            catch (RequestFailedException e)
            {
                ResponseCode statusCode;
                //This mapping is based on references : https://learn.microsoft.com/rest/api/storageservices/blob-service-error-codes
                // https://learn.microsoft.com/rest/api/storageservices/Lease-Blob?redirectedfrom=MSDN
                switch (e.Status)
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
