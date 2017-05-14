using System;
using Microsoft.Extensions.DependencyInjection;

namespace OrleansSilo
{
    public class Startup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            return services.BuildServiceProvider();
        }
    }
}