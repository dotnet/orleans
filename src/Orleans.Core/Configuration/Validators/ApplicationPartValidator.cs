using System;
using System.Linq;
using System.Reflection;

using Orleans.ApplicationParts;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans.Configuration.Validators
{
    /// <summary>
    /// Validates that application libraries (grains, serializers, etc) have been configured.
    /// </summary>
    internal class ApplicationPartValidator : IConfigurationValidator
    {
        private readonly IApplicationPartManager applicationPartManager;
        private readonly IServiceProvider serviceProvider;

        public ApplicationPartValidator(IApplicationPartManager applicationPartManager, IServiceProvider serviceProvider)
        {
            this.applicationPartManager = applicationPartManager;
            this.serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            var hasApplicationAssembly = this.applicationPartManager.ApplicationParts.OfType<AssemblyPart>().Any(part => !part.IsFrameworkAssembly);
            if (!hasApplicationAssembly)
            {
                throw new OrleansConfigurationException(
                    $"No application assemblies were added to {nameof(ApplicationPartManager)}." +
                    $" Add assemblies using the {nameof(ApplicationPartManagerExtensions.AddApplicationPart)}({nameof(Assembly)}) extension method on the client builder.");
            }

            // Ensure there is a non-framework assembly which has had code generation executed on it.
            var hasCodeGenRun = this.applicationPartManager.ApplicationParts
                .OfType<AssemblyPart>()
                .Any(part => !part.IsFrameworkAssembly && part.Assembly.GetCustomAttribute<FeaturePopulatorAttribute>() != null);
            if (!hasCodeGenRun)
            {
                throw new OrleansConfigurationException(
                    $"None of the assemblies added to {nameof(ApplicationPartManager)} contain generated code." +
                    " Ensure that code generation has been executed for grain interface and class assemblies.");
            }

            var allProviders = this.applicationPartManager.FeatureProviders;
            var nonFrameworkParts = this.applicationPartManager.ApplicationParts
                .Where(part => !(part is AssemblyPart asm) || !asm.IsFrameworkAssembly)
                .ToList();

            var isSilo = this.serviceProvider.GetService(typeof(ILocalSiloDetails)) != null;
            if (isSilo)
            {
                var providers = allProviders.OfType<IApplicationFeatureProvider<GrainClassFeature>>();
                var grains = new GrainClassFeature();
                foreach (var provider in providers)
                {
                    provider.PopulateFeature(nonFrameworkParts, grains);
                }

                var hasGrains = grains.Classes.Any();
                if (!hasGrains)
                {
                    throw new OrleansConfigurationException(
                        $"None of the assemblies added to {nameof(ApplicationPartManager)} contain generated code for grain classes." +
                        " Ensure that code generation has been executed for grain interface and grain class assemblies and that they have been added as application parts.");
                }
            }

            {
                var providers = allProviders.OfType<IApplicationFeatureProvider<GrainInterfaceFeature>>();
                var grainInterfaces = new GrainInterfaceFeature();
                foreach (var provider in providers)
                {
                    provider.PopulateFeature(nonFrameworkParts, grainInterfaces);
                }

                bool hasGrainInterfaces = grainInterfaces.Interfaces.Any();
                if (!hasGrainInterfaces)
                {
                    throw new OrleansConfigurationException(
                        $"None of the assemblies added to {nameof(ApplicationPartManager)} contain generated code for grain interfaces." +
                        " Ensure that code generation has been executed for grain interface and grain class assemblies and that they have been added as application parts.");
                }
            }
        }
    }
}