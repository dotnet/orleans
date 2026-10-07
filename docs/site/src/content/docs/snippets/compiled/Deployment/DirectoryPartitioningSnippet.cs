using System;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Documentation.Deployment;

internal static class DirectoryPartitioningSnippet
{
    internal static void Configure(ISiloBuilder siloBuilder)
    {
        // <legacy_directory_partitions>
        siloBuilder.Configure<GrainDirectoryOptions>(options =>
        {
            options.PartitionsPerSilo = 30;
            options.GetPartitionBoundaries = static (silo, partitionCount) =>
            {
                var boundaries = (uint[])silo.GetUniformHashCodes(partitionCount).Clone();
                Array.Sort(boundaries);
                return boundaries;
            };
        });
        // </legacy_directory_partitions>
    }
}
