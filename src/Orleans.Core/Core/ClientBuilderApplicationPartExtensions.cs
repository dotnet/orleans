using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Orleans.ApplicationParts;
using Orleans.Hosting;

namespace Orleans
{
    /// <summary>
    /// Extensions to <see cref="IClientBuilder"/> for managing application parts.
    /// </summary>
    public static class ClientBuilderApplicationPartExtensions
    {
        /// <summary>
        /// Returns the <see cref="ApplicationPartManager"/> for this instance.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The <see cref="ApplicationPartManager"/> for this instance.</returns>
        public static ApplicationPartManager GetApplicationPartManager(this IClientBuilder builder) => ApplicationPartManagerExtensions.GetApplicationPartManager(builder.Properties);

        /// <summary>
        /// Adds all assemblies in the current <see cref="AppDomain"/> as application parts.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="loadReferencedAssemblies">Whether or not try to load all referenced assemblies before </param>
        public static IClientBuilder AddApplicationPartsFromAppDomain(this IClientBuilder builder, bool loadReferencedAssemblies = true)
        {
            builder.GetApplicationPartManager().AddApplicationPartsFromAppDomain(loadReferencedAssemblies);
            return builder;
        }

        /// <summary>
        /// Adds all assemblies referenced by the provided <paramref name="assembly"/> as application parts.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="assembly">The assembly</param>
        public static IClientBuilder AddApplicationPartsFromReferences(this IClientBuilder builder, Assembly assembly)
        {
            builder.GetApplicationPartManager().AddApplicationPartsFromReferences(assembly);
            return builder;
        }

        /// <summary>
        /// Attempts to load all assemblies in the application base path and add them as application parts.
        /// </summary>
        /// <param name="builder">The builder.</param>
        public static IClientBuilder AddApplicationPartsFromBasePath(this IClientBuilder builder)
        {
            builder.GetApplicationPartManager().AddApplicationPartsFromBasePath();
            return builder;
        }

        /// <summary>
        /// Adds an application part.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="part">The application part.</param>
        public static IClientBuilder AddApplicationPart(this IClientBuilder builder, Assembly assembly)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            var manager = builder.GetApplicationPartManager();
            manager.AddApplicationPart(new AssemblyPart(assembly));

            return builder;
        }
    }
}