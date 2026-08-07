using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;

namespace Orleans.Docs.Snippets.Versioning;

public static class VersioningConfiguration
{
    public static void Configure()
    {
        // <configure_versioning>
        var builder = Host.CreateApplicationBuilder();

        builder.UseOrleans(siloBuilder =>
        {
            siloBuilder.Configure<GrainVersioningOptions>(options =>
            {
                options.DefaultCompatibilityStrategy = nameof(BackwardCompatible);
                options.DefaultVersionSelectorStrategy = nameof(AllCompatibleVersions);
            });
        });
        // </configure_versioning>
    }
}
