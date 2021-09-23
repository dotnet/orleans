using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;

#if ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureStorage
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureStorage
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// General utility functions related to Azure Blob storage.
    /// </summary>
    internal static class AzureBlobUtils
    {
        private static readonly Regex ContainerNameRegex = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.ExplicitCapture | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        internal static void ValidateContainerName(string containerName)
        {
            if (string.IsNullOrWhiteSpace(containerName) || containerName.Length < 3 || containerName.Length > 63 || !ContainerNameRegex.IsMatch(containerName))
            {
                throw new ArgumentException("Invalid container name", nameof(containerName));
            }
        }

        internal static void ValidateBlobName(string blobName)
        {
            if (string.IsNullOrWhiteSpace(blobName) || blobName.Length > 1024 || blobName.Count(c => c == '/') >= 254)
            {
                throw new ArgumentException("Invalid blob name", nameof(blobName));
            }
        }

        internal static async Task<BlobServiceClient> CreateBlobServiceClient(IBlobServiceClientOptions options)
        {
            BlobServiceClient client;

            if (options.CreateClient is { } createBlobClient)
            {
                ThrowIfNotNull(options.TokenCredential, nameof(options.TokenCredential), nameof(options.TokenCredential));
                ThrowIfNotNull(options.AzureSasCredential, nameof(options.AzureSasCredential), nameof(options.AzureSasCredential));
                ThrowIfNotNull(options.SharedKeyCredential, nameof(options.SharedKeyCredential), nameof(options.SharedKeyCredential));
                ThrowIfNotNull(options.ConnectionString, nameof(options.ConnectionString), nameof(options.ConnectionString));
                ThrowIfNotNull(options.ServiceUri, nameof(options.ServiceUri), nameof(options.ServiceUri));
                client = await createBlobClient();
            }
            else if (options.TokenCredential is { } tokenCredential)
            {
                ValidateUrl(options, nameof(options.TokenCredential));
                ThrowIfNotNull(options.AzureSasCredential, nameof(options.AzureSasCredential), nameof(options.AzureSasCredential));
                ThrowIfNotNull(options.SharedKeyCredential, nameof(options.SharedKeyCredential), nameof(options.SharedKeyCredential));
                ThrowIfNotNull(options.ConnectionString, nameof(options.ConnectionString), nameof(options.ConnectionString));
                client = new BlobServiceClient(options.ServiceUri, tokenCredential, options.ClientOptions);
            }
            else if (options.AzureSasCredential is { } sasCredential)
            {
                ValidateUrl(options, nameof(options.AzureSasCredential));
                ThrowIfNotNull(options.SharedKeyCredential, nameof(options.SharedKeyCredential), nameof(options.SharedKeyCredential));
                ThrowIfNotNull(options.ConnectionString, nameof(options.ConnectionString), nameof(options.ConnectionString));
                client = new BlobServiceClient(options.ServiceUri, sasCredential, options.ClientOptions);
            }
            else if (options.SharedKeyCredential is { } tableSharedKeyCredential)
            {
                ValidateUrl(options, nameof(options.SharedKeyCredential));
                ThrowIfNotNull(options.ConnectionString, nameof(options.ConnectionString), nameof(options.ConnectionString));
                client = new BlobServiceClient(options.ServiceUri, tableSharedKeyCredential, options.ClientOptions);
            }
            else if (options.ConnectionString is { Length: > 0 } connectionString)
            {
                ThrowIfNotNull(options.ServiceUri, nameof(options.ServiceUri), nameof(options.ConnectionString));
                client = new BlobServiceClient(connectionString, options.ClientOptions);
            }
            else
            {
                client = new BlobServiceClient(options.ServiceUri, options.ClientOptions);
            }

            return client;

            static void ValidateUrl(IBlobServiceClientOptions options, string dependentOption)
            {
                if (options.ServiceUri is null)
                {
                    throw new InvalidOperationException($"{nameof(options.ServiceUri)} is null, but it is required when {dependentOption} is specified");
                }
            }

            static void ThrowIfNotNull(object value, string propertyName, string dependentOption)
            {
                if (value is not null)
                {
                    throw new InvalidOperationException($"{propertyName} is not null, but it is not being used because {dependentOption} has been set and takes precedence");
                }
            }
        }

        internal interface IBlobServiceClientOptions
        {
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
    }
}
