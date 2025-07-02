using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Configuration;

namespace Orleans.Streaming.Migration.Configuration;

public class AzureQueueMigrationOptions : AzureQueueOptions
{
    public SerializationMode SerializationMode { get; set; }
}

public enum SerializationMode
{
    Default = 0,

    Json = 1,
    PrioritizeJson = 2
}
