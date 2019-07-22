using System;
using System.Threading.Tasks;

namespace Orleans.Networking.Shared
{
    internal static class ConnectionBuilderExtensions
    {
        public static IConnectionBuilder Run(this IConnectionBuilder connectionBuilder, Func<ConnectionContext, Task> middleware)
        {
            return connectionBuilder.Use(next =>
            {
                return context =>
                {
                    return middleware(context);
                };
            });
        }
    }
}
