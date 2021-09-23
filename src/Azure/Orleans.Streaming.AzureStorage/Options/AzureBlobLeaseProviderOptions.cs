using System;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streaming.AzureStorage;

namespace Orleans.Configuration
{
    public class AzureBlobLeaseProviderOptions : AzureBlobUtils.IBlobServiceClientOptions
    {
        public string BlobContainerName { get; set; } = DefaultBlobContainerName;
        public const string DefaultBlobContainerName = "Leases";

        /// <summary>
        /// The service connection string.
        /// </summary>
        /// <remarks>
        /// This property is superseded by all other properties except for <see cref="ServiceUri"/>.
        /// </remarks>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// The Service URI (e.g. https://x.blob.core.windows.net).
        /// </summary>
        /// <remarks>
        /// If this property contains a shared access signature, then no other credential properties are required.
        /// Otherwise, the presence of any other credential property will take precedence over this.
        /// </remarks>
        public Uri ServiceUri { get; set; }

        /// <summary>
        /// Token credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/>, <see cref="AzureSasCredential"/>, and <see cref="SharedKeyCredential"/>.
        /// This property is superseded by <see cref="CreateClient"/>.
        /// </remarks>
        public TokenCredential TokenCredential { get; set; }

        /// <summary>
        /// Azure SAS credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/> and <see cref="SharedKeyCredential"/>.
        /// This property is superseded by <see cref="CreateClient"/> and <see cref="TokenCredential"/>.
        /// </remarks>
        public AzureSasCredential AzureSasCredential { get; set; }

        /// <summary>
        /// Options to be used when configuring the blob storage client, or <see langword="null"/> to use the default options.
        /// </summary>
        public BlobClientOptions ClientOptions { get; set; }

        /// <summary>
        /// Shared key credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/>.
        /// This property is superseded by <see cref="CreateClient"/>, <see cref="TokenCredential"/>, and <see cref="AzureSasCredential"/>.
        /// </remarks>
        public StorageSharedKeyCredential SharedKeyCredential { get; set; }

        /// <summary>
        /// The optional delegate used to create a <see cref="BlobServiceClient"/> instance.
        /// </summary>
        /// <remarks>
        /// This property, if not <see langword="null"/>, takes precedence over <see cref="ConnectionString"/>, <see cref="SharedKeyCredential"/>, <see cref="AzureSasCredential"/>, <see cref="TokenCredential"/>, <see cref="ClientOptions"/>, and <see cref="ServiceUri"/>,
        /// </remarks>
        public Func<Task<BlobServiceClient>> CreateClient { get; set; }
    }

    /// <summary>
    /// Configuration validator for AzureBlobLeaseProviderOptions
    /// </summary>
    public class AzureBlobLeaseProviderOptionsValidator : IConfigurationValidator
    {
        private readonly AzureBlobLeaseProviderOptions options;
        private readonly string name;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        public AzureBlobLeaseProviderOptionsValidator(IOptions<AzureBlobLeaseProviderOptions> options)
        {
            this.options = options.Value;
        }

        /// <summary>
        /// Creates creates validator for named instance of AzureBlobLeaseProviderOptions options.
        /// </summary>
        /// <param name="services"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public static IConfigurationValidator Create(IServiceProvider services, string name)
        {
            var options = services.GetOptionsByName<AzureBlobLeaseProviderOptions>(name);
            return new AzureBlobLeaseProviderOptionsValidator(options, name);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        private AzureBlobLeaseProviderOptionsValidator(AzureBlobLeaseProviderOptions options, string name)
        {
            this.options = options;
            this.name = name ?? string.Empty;
        }

        public void ValidateConfiguration()
        {
            // name can be null, but not empty or white space.
            if(this.name != null && string.IsNullOrWhiteSpace(this.name))
            {
                throw new OrleansConfigurationException($"Named option {nameof(AzureBlobLeaseProviderOptions)} of name {this.name} is invalid.  Name cannot be empty or whitespace.");
            }

            if (this.options.ServiceUri == null)
            {
                if (this.options.TokenCredential != null)
                {
                    throw new OrleansConfigurationException($"Configuration for {nameof(AzureBlobLeaseProviderOptions)} of name {name} is invalid. {nameof(options.ServiceUri)} is required for {nameof(options.TokenCredential)}");
                }
            }

            try
            {
                AzureBlobUtils.ValidateContainerName(this.options.BlobContainerName);
            }
            catch (ArgumentException e)
            {
                var errorStr = string.IsNullOrEmpty(this.name)
                    ? $"Configuration for {nameof(AzureBlobLeaseProviderOptions)} {this.name} is invalid. {nameof(this.options.BlobContainerName)} is not valid"
                    : $"Configuration for {nameof(AzureBlobLeaseProviderOptions)} is invalid. {nameof(this.options.BlobContainerName)} is not valid";
                throw new OrleansConfigurationException(errorStr , e);
            }
        }
    }

}
