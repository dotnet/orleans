using System.Linq;
using System.Reflection;
using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;

namespace Orleans
{
    internal class ApplicationPartValidator : IConfigurationValidator
    {
        private readonly IApplicationPartManager applicationPartManager;

        public ApplicationPartValidator(IApplicationPartManager applicationPartManager)
        {
            this.applicationPartManager = applicationPartManager;
        }

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
        }
    }
}