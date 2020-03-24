using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Orleans.Persistence.S3.Options;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Persistence.S3.Provider
{
    public class S3GrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
    {
        private readonly string name;
        private readonly S3StorageOptions options;
        private readonly SerializationManager serializationManager;
        private readonly IAmazonS3 s3;
        private readonly IS3GrainStorageKeyFormatter keyFormatter;
        private readonly ILogger logger;

        public S3GrainStorage(
            string name,
            S3StorageOptions options,
            SerializationManager serializationManager,
            IAmazonS3 s3,
            IS3GrainStorageKeyFormatter keyFormatter,
            ILoggerFactory loggerFactory)
        {
            this.name = name;
            this.options = options;
            this.serializationManager = serializationManager;
            this.s3 = s3;
            this.keyFormatter = keyFormatter;

            this.logger = loggerFactory.CreateLogger($"{typeof(S3GrainStorage).FullName}.{name}");
        }

        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            this.logger.Trace(ErrorCode.StorageProviderBase,
                "Reading: GrainType={GrainType} GrainId={GrainId} from Bucket={BucketName}",
                grainType, grainReference, this.options.BucketName);
            try
            {
                using (var response = await this.s3.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = this.options.BucketName,
                    EtagToMatch = grainState.ETag,
                    Key = this.keyFormatter.FormatKey(this.name, grainType, grainReference)
                }).ConfigureAwait(false))
                using (response.ResponseStream)
                {
                    grainState.ETag = response.ETag;

                    var data = await response.ResponseStream
                        .AsArraySegmentAsync((int)response.ContentLength)
                        .ConfigureAwait(false);

                    this.serializationManager.DeserializeToState(grainState, data);
                }
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                grainState.State = grainState.CreateDefaultState();
            }
            catch (Exception e)
            {
                this.logger.LogError(new EventId((int)ErrorCode.StorageProviderBase), e,
                    "Error Reading: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BucketName={BucketName} Exception={Exception}",
                    grainType, grainReference, grainState.ETag, this.options.BucketName, e.Message);
                throw;
            }
        }

        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                this.logger.Trace(ErrorCode.StorageProviderBase,
                    "Writing: GrainType={GrainType} GrainId={GrainId} from Bucket={BucketName}",
                    grainType, grainReference, this.options.BucketName);
            }

            try
            {
                await this.s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = this.options.BucketName,
                    Key = this.keyFormatter.FormatKey(this.name, grainType, grainReference),
                    InputStream = this.serializationManager
                        .SerializeFromState(grainState)
                        .AsMemoryStream()
                }).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                this.logger.LogError(new EventId((int)ErrorCode.StorageProviderBase), e,
                    "Error Writing: GrainType={GrainType} GrainId={GrainId} ETag={ETag} to BucketName={BucketName} Exception={Exception}",
                    grainType, grainReference, grainState.ETag, this.options.BucketName, e.Message);
                throw;
            }
        }

        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            try
            {
                await this.s3.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = this.options.BucketName,
                    Key = this.keyFormatter.FormatKey(this.name, grainType, grainReference)
                }).ConfigureAwait(false);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Ignore not found errors
            }
        }

        public void Participate(ISiloLifecycle lifecycle) => lifecycle.Subscribe<S3GrainStorage>(this.options.InitStage, this);
        public Task OnStart(CancellationToken ct) => Task.CompletedTask;
        public Task OnStop(CancellationToken ct) => Task.CompletedTask;
    }
}