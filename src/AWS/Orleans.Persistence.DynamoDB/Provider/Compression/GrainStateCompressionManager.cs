using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Persistence.DynamoDB.Provider.Compression.Interfaces;
using Orleans.Storage;

namespace Orleans.Persistence.DynamoDB.Provider.Compression
{
    internal class GrainStateCompressionManager : IGrainStateCompressionManager
    {
        public const string CompressionPropertyName = "Compression";

        private readonly DynamoDBStorageOptions options;
        private readonly ILogger<GrainStateCompressionManager> logger;
        private readonly Dictionary<IGrainStateCompressionManager.BinaryStateCompression, IProvideGrainStateRecordCompression> compressionProviders;
        private readonly IProvideGrainStateRecordCompression compressionProviderConfigured;

        public GrainStateCompressionManager(
            DynamoDBStorageOptions options,
            IEnumerable<IProvideGrainStateRecordCompression> compressionProviders,
            ILogger<GrainStateCompressionManager> logger)
        {
            this.options = options;
            this.logger = logger;

            this.compressionProviders = compressionProviders.ToDictionary(
                compressionProvider => compressionProvider.CompressionType);

            if (this.options.StateCompressionPolicy is {IsEnabled: true}
                && !this.compressionProviders.TryGetValue(
                    this.options.StateCompressionPolicy.Compression,
                    out this.compressionProviderConfigured))
            {
                throw new ArgumentException($"Unable to resolve the compression provider for compression of type: {this.options.StateCompressionPolicy.Compression}");
            }
        }

        public void Compress(DynamoDBGrainStorage.GrainStateRecord record)
        {
            if (record?.BinaryStateProperties == null)
            {
                throw new ArgumentException("record or record.StateProperties is null", nameof(record));
            }

            if (record.BinaryStateProperties.ContainsKey(CompressionPropertyName))
            {
                this.logger.LogWarning("State properties already contains the compression property {0}", CompressionPropertyName);
                return;
            }

            if (this.options.StateCompressionPolicy?.IsEnabled == true
                && this.options.StateCompressionPolicy.CompressStateIfAboveByteCount < record.BinaryState.Length)
            {
                this.compressionProviderConfigured.Compress(record);
            }
        }

        public void Decompress(DynamoDBGrainStorage.GrainStateRecord record)
        {
            if (!record.BinaryStateProperties.TryGetValue(
                    CompressionPropertyName,
                    out var recordCompressionTypeProperty))
            {
                return;
            }

            if (!Enum.TryParse<IGrainStateCompressionManager.BinaryStateCompression>(
                    recordCompressionTypeProperty,
                    true,
                    out var recordCompressionType))
            {
                throw new ArgumentException($"Unable to parse the record compression property to {nameof(IGrainStateCompressionManager.BinaryStateCompression)}");
            }

            if (!this.compressionProviders.TryGetValue(recordCompressionType, out var compressionProvider))
            {
                throw new ArgumentException("Unable to determine the record compression provider");
            }

            compressionProvider.Decompress(record);
        }
    }
}
