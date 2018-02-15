// Code from https://github.com/aspnet/Options/blob/fe3f1b15811958acfa0be7eb88656d4bd5943834/src/Microsoft.Extensions.Options/OptionsServiceCollectionExtensions.cs
// This will be removed and superseded Mirosoft.Extensions.Options v2.1.0.0 once it ships.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// Extension methods for adding options services to the DI container. This will be deprecated and superseded Mirosoft.Extensions.Options v2.1.0.0 once it ships.
    /// </summary>
    public static class OptionsServiceCollectionExtensions
    {
        /// <summary>
        /// Gets an options builder that forwards Configure calls for the same <typeparamref name="TOptions"/> to the underlying service collection. This will be deprecated and superseded Mirosoft.Extensions.Options v2.1.0.0 once it ships.
        /// </summary>
        /// <typeparam name="TOptions">The options type to be configured.</typeparam>
        /// <param name="services">The <see cref="IServiceCollection"/> to add the services to.</param>
        /// <returns>The <see cref="OptionsBuilder{TOptions}"/> so that configure calls can be chained in it.</returns>
        public static OptionsBuilder<TOptions> AddOptions<TOptions>(this IServiceCollection services)
            where TOptions : class
            => services.AddOptions<TOptions>(Options.DefaultName);

        /// <summary>
        /// Gets an options builder that forwards Configure calls for the same named <typeparamref name="TOptions"/> to the underlying service collection. This will be deprecated and superseded Mirosoft.Extensions.Options v2.1.0.0 once it ships.
        /// </summary>
        /// <typeparam name="TOptions">The options type to be configured.</typeparam>
        /// <param name="services">The <see cref="IServiceCollection"/> to add the services to.</param>
        /// <param name="name">The name of the options instance.</param>
        /// <returns>The <see cref="OptionsBuilder{TOptions}"/> so that configure calls can be chained in it.</returns>
        public static OptionsBuilder<TOptions> AddOptions<TOptions>(this IServiceCollection services, string name)
            where TOptions : class
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            services.AddOptions();

            return new OptionsBuilder<TOptions>(services, name);
        }
    }
}