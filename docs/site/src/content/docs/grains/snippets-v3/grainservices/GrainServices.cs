using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Core;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Services;
using Orleans.Services;

namespace GrainServices;

// Grain service interface
public interface IDataService : IGrainService
{
    Task MyMethod();
}

// <data_service>
[Reentrant]
public class DataService : GrainService, IDataService
{
    readonly IGrainFactory _grainFactory;

    public DataService(
        IServiceProvider services,
        IGrainIdentity id,
        Silo silo,
        ILoggerFactory loggerFactory,
        IGrainFactory grainFactory)
        : base(id, silo, loggerFactory)
    {
        _grainFactory = grainFactory;
    }

    public override Task Init(IServiceProvider serviceProvider) =>
        base.Init(serviceProvider);

    public override Task Start() => base.Start();

    public override Task Stop() => base.Stop();

    public Task MyMethod()
    {
        // TODO: custom logic here.
        return Task.CompletedTask;
    }
}
// </data_service>

// Grain service client interface
public interface IDataServiceClient : IGrainServiceClient<IDataService>, IDataService
{
}

// Grain service client implementation - simplified for Orleans 3.x
public class DataServiceClient : GrainServiceClient<IDataService>, IDataServiceClient
{
    public DataServiceClient(IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
    }

    public Task MyMethod() => GrainService.MyMethod();
}

public class GrainServiceConfiguration
{
    // <configure_grain_service>
    public static void ConfigureGrainService(ISiloHostBuilder builder)
    {
        builder.ConfigureServices(
            services => services.AddGrainService<DataService>()
                                .AddSingleton<IDataServiceClient, DataServiceClient>());
    }
    // </configure_grain_service>
}
