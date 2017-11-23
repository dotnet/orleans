// Code from https://github.com/aspnet/Options/blob/edc21af4166574ecd45d8f8dbd381dfec044f367/src/Microsoft.Extensions.Options/OptionsBuilder.cs
// This will be removed and superseded Mirosoft.Extensions.Options v2.1.0.0 once it ships.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Used to configure TOptions instances. This will be deprecated and superseded Mirosoft.Extensions.Options v2.1.0.0 once it ships.
    /// </summary>
    /// <typeparam name="TOptions">The type of options being requested.</typeparam>
    public class OptionsBuilder<TOptions> where TOptions : class
    {
        /// <summary>
        /// The default name of the TOptions instance.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The <see cref="IServiceCollection"/> for the options being configured.
        /// </summary>
        public IServiceCollection Services { get; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> for the options being configured.</param>
        /// <param name="name">The default name of the TOptions instance, if null Options.DefaultName is used.</param>
        public OptionsBuilder(IServiceCollection services, string name)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            Services = services;
            Name = name ?? Options.DefaultName;
        }

        /// <summary>
        /// Registers an action used to configure a particular type of options.
        /// Note: These are run before all <seealso cref="PostConfigure(Action{TOptions})"/>.
        /// </summary>
        /// <param name="configureOptions">The action used to configure the options.</param>
        public virtual OptionsBuilder<TOptions> Configure(Action<TOptions> configureOptions)
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddSingleton<IConfigureOptions<TOptions>>(new ConfigureNamedOptions<TOptions>(Name, configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> Configure<TDep>(Action<TOptions, TDep> configureOptions)
            where TDep : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IConfigureOptions<TOptions>>(sp =>
                new ConfigureNamedOptions<TOptions, TDep>(Name, sp.GetRequiredService<TDep>(), configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> Configure<TDep1, TDep2>(Action<TOptions, TDep1, TDep2> configureOptions)
            where TDep1 : class
            where TDep2 : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IConfigureOptions<TOptions>>(sp =>
                new ConfigureNamedOptions<TOptions, TDep1, TDep2>(Name, sp.GetRequiredService<TDep1>(), sp.GetRequiredService<TDep2>(), configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> Configure<TDep1, TDep2, TDep3>(Action<TOptions, TDep1, TDep2, TDep3> configureOptions)
            where TDep1 : class
            where TDep2 : class
            where TDep3 : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IConfigureOptions<TOptions>>(
                sp => new ConfigureNamedOptions<TOptions, TDep1, TDep2, TDep3>(
                    Name,
                    sp.GetRequiredService<TDep1>(),
                    sp.GetRequiredService<TDep2>(),
                    sp.GetRequiredService<TDep3>(),
                    configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> Configure<TDep1, TDep2, TDep3, TDep4>(Action<TOptions, TDep1, TDep2, TDep3, TDep4> configureOptions)
            where TDep1 : class
            where TDep2 : class
            where TDep3 : class
            where TDep4 : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IConfigureOptions<TOptions>>(
                sp => new ConfigureNamedOptions<TOptions, TDep1, TDep2, TDep3, TDep4>(
                    Name,
                    sp.GetRequiredService<TDep1>(),
                    sp.GetRequiredService<TDep2>(),
                    sp.GetRequiredService<TDep3>(),
                    sp.GetRequiredService<TDep4>(),
                    configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> Configure<TDep1, TDep2, TDep3, TDep4, TDep5>(Action<TOptions, TDep1, TDep2, TDep3, TDep4, TDep5> configureOptions)
            where TDep1 : class
            where TDep2 : class
            where TDep3 : class
            where TDep4 : class
            where TDep5 : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IConfigureOptions<TOptions>>(
                sp => new ConfigureNamedOptions<TOptions, TDep1, TDep2, TDep3, TDep4, TDep5>(
                    Name,
                    sp.GetRequiredService<TDep1>(),
                    sp.GetRequiredService<TDep2>(),
                    sp.GetRequiredService<TDep3>(),
                    sp.GetRequiredService<TDep4>(),
                    sp.GetRequiredService<TDep5>(),
                    configureOptions));
            return this;
        }

        /// <summary>
        /// Registers an action used to configure a particular type of options.
        /// Note: These are run after all <seealso cref="Configure(Action{TOptions})"/>.
        /// </summary>
        /// <param name="configureOptions">The action used to configure the options.</param>
        public virtual OptionsBuilder<TOptions> PostConfigure(Action<TOptions> configureOptions)
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddSingleton<IPostConfigureOptions<TOptions>>(new PostConfigureOptions<TOptions>(Name, configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> PostConfigure<TDep>(Action<TOptions, TDep> configureOptions)
            where TDep : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IPostConfigureOptions<TOptions>>(sp =>
                new PostConfigureOptions<TOptions, TDep>(Name, sp.GetRequiredService<TDep>(), configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> PostConfigure<TDep1, TDep2>(Action<TOptions, TDep1, TDep2> configureOptions)
            where TDep1 : class
            where TDep2 : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IPostConfigureOptions<TOptions>>(sp =>
                new PostConfigureOptions<TOptions, TDep1, TDep2>(Name, sp.GetRequiredService<TDep1>(), sp.GetRequiredService<TDep2>(), configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> PostConfigure<TDep1, TDep2, TDep3>(Action<TOptions, TDep1, TDep2, TDep3> configureOptions)
            where TDep1 : class
            where TDep2 : class
            where TDep3 : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IPostConfigureOptions<TOptions>>(
                sp => new PostConfigureOptions<TOptions, TDep1, TDep2, TDep3>(
                    Name,
                    sp.GetRequiredService<TDep1>(),
                    sp.GetRequiredService<TDep2>(),
                    sp.GetRequiredService<TDep3>(),
                    configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> PostConfigure<TDep1, TDep2, TDep3, TDep4>(Action<TOptions, TDep1, TDep2, TDep3, TDep4> configureOptions)
            where TDep1 : class
            where TDep2 : class
            where TDep3 : class
            where TDep4 : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IPostConfigureOptions<TOptions>>(
                sp => new PostConfigureOptions<TOptions, TDep1, TDep2, TDep3, TDep4>(
                    Name,
                    sp.GetRequiredService<TDep1>(),
                    sp.GetRequiredService<TDep2>(),
                    sp.GetRequiredService<TDep3>(),
                    sp.GetRequiredService<TDep4>(),
                    configureOptions));
            return this;
        }

        public virtual OptionsBuilder<TOptions> PostConfigure<TDep1, TDep2, TDep3, TDep4, TDep5>(Action<TOptions, TDep1, TDep2, TDep3, TDep4, TDep5> configureOptions)
            where TDep1 : class
            where TDep2 : class
            where TDep3 : class
            where TDep4 : class
            where TDep5 : class
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            Services.AddTransient<IPostConfigureOptions<TOptions>>(
                sp => new PostConfigureOptions<TOptions, TDep1, TDep2, TDep3, TDep4, TDep5>(
                    Name,
                    sp.GetRequiredService<TDep1>(),
                    sp.GetRequiredService<TDep2>(),
                    sp.GetRequiredService<TDep3>(),
                    sp.GetRequiredService<TDep4>(),
                    sp.GetRequiredService<TDep5>(),
                    configureOptions));
            return this;
        }
    }
}