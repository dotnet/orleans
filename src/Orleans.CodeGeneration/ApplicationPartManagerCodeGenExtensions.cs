using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
        /// Generates support code for the provided assembly and adds it to the builder.
        /// </summary>
        /// <param name="manager">The builder.</param>
        /// <param name="loggerFactory">The optional logger factory, for outputting code generation diagnostics.</param>
        /// <returns>A builder with support parts added.</returns>
        public static IApplicationPartManagerWithAssemblies WithCodeGeneration(this IApplicationPartManagerWithAssemblies manager, ILoggerFactory loggerFactory = null)
        {
            var stopWatch = Stopwatch.StartNew();
            loggerFactory = loggerFactory ?? new NullLoggerFactory();
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
            
            var codeGenerator = new RoslynCodeGenerator(tempPartManager, loggerFactory);
            var generatedAssembly = codeGenerator.GenerateAndLoadForAssemblies(manager.Assemblies);
            stopWatch.Stop();
            var logger = loggerFactory.CreateLogger("Orleans.CodeGenerator.RuntimeCodeGen");
            logger?.LogInformation(0, $"Runtime code generation for assemblies {String.Join(",", manager.Assemblies.ToStrings())} took {stopWatch.ElapsedMilliseconds} milliseconds");
            return manager.AddApplicationPart(generatedAssembly);
        }
    }
}
