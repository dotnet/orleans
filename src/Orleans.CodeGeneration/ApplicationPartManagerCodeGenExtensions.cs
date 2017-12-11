using Microsoft.Extensions.Logging.Abstractions;
using Orleans.ApplicationParts;
using Orleans.CodeGenerator;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for <see cref="IApplicationPartManagerWithAssemblies"/> for invoking code generation.
    /// </summary>
    public static class ApplicationPartManagerCodeGenExtensions
    {
        /// <summary>
        /// Generates support code for the the provided assembly and adds it to the builder.
        /// </summary>
        /// <param name="manager">The builder.</param>
        /// <returns>A builder with support parts added.</returns>
        public static IApplicationPartManagerWithAssemblies WithCodeGeneration(this IApplicationPartManagerWithAssemblies manager)
        {
            var tempPartManager = new ApplicationPartManager();
            foreach (var provider in manager.FeatureProviders)
            {
                tempPartManager.AddFeatureProvider(provider);
            }

            foreach (var part in manager.ApplicationParts)
            {
                tempPartManager.AddApplicationPart(part);
            }

            tempPartManager.AddApplicationPart(new AssemblyPart(typeof(RuntimeVersion).Assembly));
            tempPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainInterfaceFeature>());
            tempPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<GrainClassFeature>());
            tempPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>());
            tempPartManager.AddFeatureProvider(new BuiltInTypesSerializationFeaturePopulator());
            
            var codeGenerator = new RoslynCodeGenerator(tempPartManager, new NullLoggerFactory());
            var generatedAssembly = codeGenerator.GenerateAndLoadForAssemblies(manager.Assemblies);

            return manager.AddApplicationPart(generatedAssembly);
        }
    }
}
