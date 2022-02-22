using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;

namespace Orleans.Configuration
{
    /// <summary>
    /// Extension methods on <see cref="IServiceCollection"/>, to provider better usability to IOptionFormatter.
    /// </summary>
    public static class OptionConfigureExtensionMethods
    {
        /// <summary>
        /// Configures an options formatter for <typeparamref name="TOptions"/>.
        /// </summary>
        /// <param name="services">
        /// The services.
        /// </param>
        /// <typeparam name="TOptions">
        /// The options type.
        /// </typeparam>
        /// <typeparam name="TOptionFormatter">
        /// The option formatter type.
        /// </typeparam>
        /// <returns>
        /// The <see cref="IServiceCollection"/>, for chaining with other calls.
        /// </returns>
        public static IServiceCollection ConfigureFormatter<TOptions, TOptionFormatter>(this IServiceCollection services)
            where TOptions : class
            where TOptionFormatter : class, IOptionFormatter<TOptions>
        {
            if (services.All(service => service.ServiceType != typeof(IOptionFormatter<TOptions>)))
            {
                services
                    .AddSingleton<IOptionFormatter<TOptions>, TOptionFormatter>()
                    .AddFromExisting<IOptionFormatter, IOptionFormatter<TOptions>>();
            }
            else
            {
                // override IOptionFormatter<TOptions>
                services.AddSingleton<IOptionFormatter<TOptions>, TOptionFormatter>();
            }
            return services;
        }

        /// <summary>
        /// Configures an options formatter for <typeparamref name="TOptions"/>.
        /// </summary>
        /// <remarks>
        /// This will use the default options formatter unless a non-default formatter is configured.
        /// </remarks>
        /// <param name="services">
        /// The services.
        /// </param>
        /// <typeparam name="TOptions">
        /// The options type.
        /// </typeparam>
        /// <returns>
        /// The <see cref="IServiceCollection"/>, for chaining with other calls.
        /// </returns>
        public static IServiceCollection ConfigureFormatter<TOptions>(this IServiceCollection services)
            where TOptions : class, new()
        {
            return services.AddSingleton<IOptionFormatter>(sp => sp.GetService<IOptionFormatter<TOptions>>());
        }

        /// <summary>
        /// Configures an options formatter for <typeparamref name="TOptions"/> if none are already configured.
        /// </summary>
        /// <param name="services">
        /// The services.
        /// </param>
        /// <typeparam name="TOptions">
        /// The options type.
        /// </typeparam>
        /// <typeparam name="TOptionFormatter">
        /// The option formatter type.
        /// </typeparam>
        /// <returns>
        /// The <see cref="IServiceCollection"/>, for chaining with other calls.
        /// </returns>
        public static IServiceCollection TryConfigureFormatter<TOptions, TOptionFormatter>(this IServiceCollection services)
            where TOptions : class
            where TOptionFormatter : class, IOptionFormatter<TOptions>
        {
            if (services.All(service => service.ServiceType != typeof(IOptionFormatter<TOptions>)))
            {
                services.ConfigureFormatter<TOptions, TOptionFormatter>();
            }

            return services;
        }

        /// <summary>
        /// Configures an options formatter resolver for <typeparamref name="TOptions"/>.
        /// </summary>
        /// <param name="services">
        /// The services.
        /// </param>
        /// <typeparam name="TOptions">
        /// The options type.
        /// </typeparam>
        /// <typeparam name="TOptionFormatterResolver">
        /// The option formatter resolver type.
        /// </typeparam>
        /// <returns>
        /// The <see cref="IServiceCollection"/>, for chaining with other calls.
        /// </returns>
        public static IServiceCollection ConfigureFormatterResolver<TOptions, TOptionFormatterResolver>(this IServiceCollection services)
            where TOptions : class
            where TOptionFormatterResolver : class, IOptionFormatterResolver<TOptions>
        {
            return services.AddSingleton<IOptionFormatterResolver<TOptions>, TOptionFormatterResolver>();
        }

        /// <summary>
        /// Configure option formatter resolver for named option TOptions, if none is configured
        /// </summary>
        public static IServiceCollection TryConfigureFormatterResolver<TOptions, TOptionFormatterResolver>(this IServiceCollection services)
            where TOptions : class
            where TOptionFormatterResolver : class, IOptionFormatterResolver<TOptions>
        {
            if (services.All(service => service.ServiceType != typeof(IOptionFormatterResolver<TOptions>)))
            {
                return services.ConfigureFormatterResolver<TOptions, TOptionFormatterResolver>();
            }

            return services;
        }

        /// <summary>
        /// Configures a named option to be logged.
        /// </summary>
        /// <typeparam name="TOptions">
        /// The option object's type.
        /// </typeparam>
        /// <param name="services">
        /// The services.
        /// </param>
        /// <param name="name">
        /// The option object's name.
        /// </param>
        /// <returns>
        /// The <see cref="IServiceCollection"/>, for chaining with other calls.
        /// </returns>
        public static IServiceCollection ConfigureNamedOptionForLogging<TOptions>(this IServiceCollection services, string name)
            where TOptions : class
        {
            return services.AddSingleton<IOptionFormatter>(sp => sp.GetService<IOptionFormatterResolver<TOptions>>().Resolve(name));
        }
    }
}
