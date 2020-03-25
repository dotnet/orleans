using System;

namespace Orleans.Configuration
{
    public abstract class AzureStorageOptions
    {
        public TimeSpan CreationTimeout { get; set; }
        public TimeSpan OperationTimeout { get; set; }
    }
}
