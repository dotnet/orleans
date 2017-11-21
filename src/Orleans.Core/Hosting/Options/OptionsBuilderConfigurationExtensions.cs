// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for adding configuration related options services to the DI container via <see cref="OptionsBuilder{TOptions}"/>. This will be deprecated and superseded Mirosoft.Extensions.Options v2.1.0.0 once it ships.
    /// </summary>
    public static class OptionsBuilderConfigurationExtensions
    {
        /// <summary>
        /// Registers a configuration instance which TOptions will bind against.
        /// </summary>
        /// <typeparam name="TOptions">The options type to be configured.</typeparam>
        /// <param name="optionsBuilder">The options builder to add the services to.</param>
        /// <param name="config">The configuration being bound.</param>
        /// <returns>The <see cref="OptionsBuilder{TOptions}"/> so that additional calls can be chained.</returns>
        public static OptionsBuilder<TOptions> Bind<TOptions>(this OptionsBuilder<TOptions> optionsBuilder, IConfiguration config) where TOptions : class
        {
            if (optionsBuilder == null)
            {
                throw new ArgumentNullException(nameof(optionsBuilder));
            }

            optionsBuilder.Services.Configure<TOptions>(optionsBuilder.Name, config);
            return optionsBuilder;
        }
    }
}