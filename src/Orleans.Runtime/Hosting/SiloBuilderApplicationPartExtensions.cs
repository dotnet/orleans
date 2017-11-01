using System;
using System.IO;
using System.Reflection;
using Orleans.ApplicationParts;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions to <see cref="ISiloHostBuilder"/> for managing application parts.
    /// </summary>
    public static class SiloBuilderApplicationPartExtensions
    {
        /// <summary>
        /// Returns the <see cref="ApplicationPartManager"/> for this instance.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The <see cref="ApplicationPartManager"/> for this instance.</returns>
        public static ApplicationPartManager GetApplicationPartManager(this ISiloHostBuilder builder) => ApplicationPartManagerExtensions.GetApplicationPartManager(builder.Properties);

        /// <summary>
        /// Adds all assemblies in the current <see cref="AppDomain"/> as application parts.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="loadReferencedAssemblies">Whether or not try to load all referenced assemblies before </param>
        public static ISiloHostBuilder AddApplicationPartsFromAppDomain(this ISiloHostBuilder builder, bool loadReferencedAssemblies = true)
        {
            builder.GetApplicationPartManager().AddApplicationPartsFromAppDomain(loadReferencedAssemblies);
            return builder;
        }

        /// <summary>
        /// Adds all assemblies referenced by the provided <paramref name="assembly"/> as application parts.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="assembly">The assembly</param>
        public static ISiloHostBuilder AddApplicationPartsFromReferences(this ISiloHostBuilder builder, Assembly assembly)
        {
            builder.GetApplicationPartManager().AddApplicationPartsFromReferences(assembly);
            return builder;
        }

        /// <summary>
        /// Attempts to load all assemblies in the application base path and add them as application parts.
        /// </summary>
        /// <param name="builder">The builder.</param>
        public static ISiloHostBuilder AddApplicationPartsFromBasePath(this ISiloHostBuilder builder)
        {
            builder.GetApplicationPartManager().AddApplicationPartsFromBasePath();
            return builder;
        }

        /// <summary>
        /// Adds an application part.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="part">The application part.</param>
        public static ISiloHostBuilder AddApplicationPart(this ISiloHostBuilder builder, Assembly assembly)
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