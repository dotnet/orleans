using Microsoft.Extensions.Options;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Text;

namespace Orleans
{
    public static class NamedOptionExtensions
    {
        public static TOption GetOptionsByName<TOption>(this IServiceProvider services, string name)
            where TOption : class, new()
        {
            return services.GetService<IOptionsSnapshot<TOption>>().Get(name);
        }
    }
}
