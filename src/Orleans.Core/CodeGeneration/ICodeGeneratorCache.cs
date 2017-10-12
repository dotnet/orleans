namespace Orleans.CodeGeneration
{
    using System.Collections.Generic;

    /// <summary>
    /// Methods for interacting with a cache for generated assemblies.
    /// </summary>
    public interface ICodeGeneratorCache
    {
        /// <summary>
        /// Adds a pre-generated assembly.
        /// </summary>
        /// <param name="targetAssemblyName">
        /// The name of the assembly the provided <paramref name="generatedAssembly"/> targets.
        /// </param>
        /// <param name="generatedAssembly">
        /// The generated assembly.
        /// </param>
        void AddGeneratedAssembly(string targetAssemblyName, GeneratedAssembly generatedAssembly);

        /// <summary>
        /// Returns the collection of generated assemblies as pairs of target assembly name to raw assembly bytes.
        /// </summary>
        /// <returns>The collection of generated assemblies.</returns>
        /// <remarks>
        /// The key of the returned dictionary is the name of the assembly which the value targets.
        /// </remarks>
        IDictionary<string, GeneratedAssembly> GetGeneratedAssemblies();
    }
}