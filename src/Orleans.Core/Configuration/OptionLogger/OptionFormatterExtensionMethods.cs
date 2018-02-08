using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Orleans.Configuration;

namespace Orleans
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
                services.AddSingleton<IOptionFormatter<TOptions>, TOptionFormatter>()
                        .AddFromExisting<IOptionFormatter, IOptionFormatter<TOptions>>();
            } else
            {
                // override IOptionFormatter<TOptions>
                services.AddSingleton<IOptionFormatter<TOptions>, TOptionFormatter>();
            }
            return services;
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
                services.AddSingleton<IOptionFormatter<TOptions>, TOptionFormatter>()
                        .AddFromExisting<IOptionFormatter, IOptionFormatter<TOptions>>();
            return services;
        }

        /// <summary>
        /// Configure option formatter resolver for named option TOptions
        /// </summary>
        public static IServiceCollection ConfigureFormatter<TOptions, TOptionFormatterResolver>(this IServiceCollection services, string name)
            where TOptions : class
            where TOptionFormatterResolver : class, IOptionFormatterResolver<TOptions>
        {
            services.AddSingleton<IOptionFormatterResolver<TOptions>, TOptionFormatterResolver>()
                    .AddSingleton<IOptionFormatter>(sp => sp.GetService<IOptionFormatterResolver<TOptions>>().Resolve(name));
            return services;
        }

        /// <summary>
        /// Configure option formatter resolver for named option TOptions, if none is configured
        /// </summary>
        public static IServiceCollection TryConfigureFormatter<TOptions, TOptionFormatterResolver>(this IServiceCollection services, string name)
            where TOptions : class
            where TOptionFormatterResolver : class, IOptionFormatterResolver<TOptions>
        {
            var registration = services.FirstOrDefault(service => service.ServiceType == typeof(IOptionFormatterResolver<TOptions>));
            if (registration == null)
                services.AddSingleton<IOptionFormatterResolver<TOptions>, TOptionFormatterResolver>();
            services.AddSingleton<IOptionFormatter>(sp => sp.GetService<IOptionFormatterResolver<TOptions>>().Resolve(name));
            return services;
        }
    }
}
