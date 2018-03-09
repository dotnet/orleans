﻿using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Configuration
{
    public class AzureTableStreamCheckpointerOptions
    {
        /// <summary>
        /// Azure table storage connections string.
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }
        /// <summary>
        /// Azure table name.
        /// </summary>
        public string TableName { get; set; }
        /// <summary>
        /// Interval to write checkpoints.  Prevents spamming storage.
        /// </summary>
        public TimeSpan PersistInterval { get; set; } = DEFAULT_CHECKPOINT_PERSIST_INTERVAL;
        public static readonly TimeSpan DEFAULT_CHECKPOINT_PERSIST_INTERVAL = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Unique namespace for checkpoint data.  Is similar to consumer group.
        /// </summary>
        public string Namespace { get; set; }
    }

    //TOOD: how to wire this validator into DI?
    public class AzureTableStreamCheckpointerOptionsValidator : IConfigurationValidator
    {
        private readonly AzureTableStreamCheckpointerOptions options;
        private string name;
        public AzureTableStreamCheckpointerOptionsValidator(AzureTableStreamCheckpointerOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }
        public void ValidateConfiguration()
        {
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new OrleansConfigurationException($"{nameof(AzureTableStreamCheckpointerOptions)} with name {this.name} is invalid. {nameof(AzureTableStreamCheckpointerOptions.ConnectionString)} is invalid");
            if (String.IsNullOrEmpty(options.TableName))
                throw new OrleansConfigurationException($"{nameof(AzureTableStreamCheckpointerOptions)} with name {this.name} is invalid. {nameof(AzureTableStreamCheckpointerOptions.TableName)} is invalid");
            if (String.IsNullOrEmpty(options.Namespace))
                throw new OrleansConfigurationException($"{nameof(AzureTableStreamCheckpointerOptions)} with name {this.name} is invalid. {nameof(AzureTableStreamCheckpointerOptions.Namespace)} is invalid");
        }
    }
}
