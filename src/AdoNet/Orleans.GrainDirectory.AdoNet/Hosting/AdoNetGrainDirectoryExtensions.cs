using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.AdoNet;

namespace Orleans.Hosting;

public static class AdoNetGrainDirectorySiloBuilderExtensions
{
    public static ISiloBuilder UseAdoNetGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<AdoNetGrainDirectoryOptions> configureOptions)
    {
        return builder.UseAdoNetGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));
    }

    public static ISiloBuilder UseAdoNetGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<AdoNetGrainDirectoryOptions>> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddAdoNetGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));
    }

    public static ISiloBuilder AddAdoNetGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<AdoNetGrainDirectoryOptions> configureOptions)
    {
        return builder.AddAdoNetGrainDirectory(name, ob => ob.Configure(configureOptions));
    }

    public static ISiloBuilder AddAdoNetGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<AdoNetGrainDirectoryOptions>> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddAdoNetGrainDirectory(name, configureOptions));
    }
}
