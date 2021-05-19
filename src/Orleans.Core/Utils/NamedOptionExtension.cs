using Microsoft.Extensions.Options;
using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans
{
    public static class NamedOptionExtensions
    {
        public static TOption GetOptionsByName<TOption>(this IServiceProvider services, string name)
            where TOption : class, new()
        {
            return services.GetRequiredService<IOptionsMonitor<TOption>>().Get(name);
        }
    }
}
