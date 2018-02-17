using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Linq;
namespace Orleans.Configuration
{
    /// <summary>
    /// Extension methods on IServiceCollection, to provider better usability to IOptionFormatter
    /// </summary>
    public static class OptionConfigureExtensionMethods
    {
        /// <summary>
        /// configure option formatter for <typeparam name="TOptions"/>
        /// </summary>
        public static IServiceCollection ConfigureFormatter<TOptions, TOptionFormatter>(this IServiceCollection services)
            where TOptions : class
            where TOptionFormatter : class, IOptionFormatter<TOptions>
        {
            var registration = services.FirstOrDefault(service => service.ServiceType == typeof(IOptionFormatter<TOptions>));
            if (registration == null)
            {
                services
                    .AddSingleton<IOptionFormatter<TOptions>, TOptionFormatter>()
                    .AddFromExisting<IOptionFormatter, IOptionFormatter<TOptions>>();
            } else
            {
                // override IOptionFormatter<TOptions>
                services.AddSingleton<IOptionFormatter<TOptions>, TOptionFormatter>();
            }
            return services;
        }

        /// <summary>
        /// configure option formatter for <typeparam name="TOptions"/>
        /// </summary>
        public static IServiceCollection ConfigureFormatter<TOptions>(this IServiceCollection services)
            where TOptions : class, new()
        {
            return services.AddSingleton<IOptionFormatter>(sp => sp.GetService<IOptionFormatter<TOptions>>());
        }

        /// <summary>
        /// Configure a option formatter for option <typeparam name="TOptions"/> if none is configured
        /// </summary>
        /// <typeparam name="TOptions"></typeparam>
        /// <typeparam name="TOptionFormatter"></typeparam>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection TryConfigureFormatter<TOptions, TOptionFormatter>(this IServiceCollection services)
            where TOptions : class
            where TOptionFormatter : class, IOptionFormatter<TOptions>
        {
            var registration = services.FirstOrDefault(service => service.ServiceType == typeof(IOptionFormatter<TOptions>));
            if (registration == null)
                services.ConfigureFormatter<TOptions, TOptionFormatter>();
            return services;
        }

        /// <summary>
        /// Configure a option formatter for option <typeparam name="TOptions"/> if none is configured
        /// </summary>
        /// <typeparam name="TOptions"></typeparam>
        /// <typeparam name="TOptionFormatter"></typeparam>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection TryConfigureFormatter<TOptions>(this IServiceCollection services)
            where TOptions : class, new()
        {
            services.TryAddSingleton<IOptionFormatter>(sp => sp.GetService<IOptionFormatter<TOptions>>());
            return services;
        }

        /// <summary>
        /// Configure option formatter resolver for named option TOptions
        /// </summary>
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
            var registration = services.FirstOrDefault(service => service.ServiceType == typeof(IOptionFormatterResolver<TOptions>));
            if (registration == null)
                return services.ConfigureFormatterResolver<TOptions, TOptionFormatterResolver>();
            return services;
        }

        /// <summary>
        /// Configure a named option to be logged
        /// </summary>
        public static IServiceCollection ConfigureNamedOptionForLogging<TOptions>(this IServiceCollection services, string name)
            where TOptions : class
        {
            return services.AddSingleton<IOptionFormatter>(sp => sp.GetService<IOptionFormatterResolver<TOptions>>().Resolve(name));
        }

    }
}
