using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Orleans.Streaming.Redis.Streams;

internal class RedisStreamServiceProvider(IServiceProvider serviceProvider, string name) : IServiceProvider
{
    public string Name => name;

    public object GetService(Type serviceType)
    {
        return serviceProvider.GetService(serviceType);
    }

    public TService GetComponentService<TService>() where TService : notnull
    {
        return serviceProvider.GetRequiredKeyedService<TService>(Name);
    }

    public TOption GetOptions<TOption>()
        where TOption : class, new()
    {
        return serviceProvider
            .GetRequiredService<IOptionsMonitor<TOption>>()
            .Get(Name);
    }
}
