namespace Orleans.CodeGenerator
{
    using System.Reflection;

    using Orleans.CodeGeneration;

    public class CodeGenerator : IRuntimeCodeGenerator, ISourceCodeGenerator
    {
        public static void GenerateAndLoadForAllAssemblies()
        {
            GrainMethodInvokerGenerator.CreateForCurrentlyLoadedAssemblies();
            GrainReferenceGenerator.CreateForCurrentlyLoadedAssemblies();
            GrainStateGenerator.CreateForCurrentlyLoadedAssemblies();
            SerializerGenerator.CreateForCurrentlyLoadedAssemblies();
        }

        public void GenerateAndLoadForAssembly(Assembly input)
        {
            if (input.IsDynamic)
            {
                return;
            }

            GrainReferenceGenerator.GenerateAndLoadForAssembly(input);
            GrainMethodInvokerGenerator.GenerateAndLoadForAssembly(input);
            GrainStateGenerator.GenerateAndLoadForAssembly(input);
            SerializerGenerator.GenerateAndLoadForAssembly(input);
        }

        public string GenerateSourceForAssembly(Assembly input)
        {
            var methodInvokers = GrainMethodInvokerGenerator.GenerateSourceForAssembly(input);
            var grainReferences = GrainReferenceGenerator.GenerateSourceForAssembly(input);
            var grainState = GrainStateGenerator.GenerateSourceForAssembly(input);
            var serializers = SerializerGenerator.GenerateSourceForAssembly(input);
            return string.Format("{0}\n{1}\n{2}\n{3}", methodInvokers, grainReferences, grainState, serializers);
        }
    }
}
