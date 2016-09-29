namespace Orleans.CodeGeneration
{
    using System.Reflection;

    /// <summary>
    /// Methods for generating code at runtime.
    /// </summary>
    internal interface IRuntimeCodeGenerator
    {
        /// <summary>
        /// Ensures that code generation has been run for the provided assembly.
        /// </summary>
        /// <param name="input">
        /// The assembly to generate code for.
        /// </param>
        void GenerateAndLoadForAssembly(Assembly input);

        /// <summary>
        /// Generates and loads code for the specified inputs.
        /// </summary>
        /// <param name="inputs">The assemblies to generate code for.</param>
        void GenerateAndLoadForAssemblies(params Assembly[] inputs);
    }
}
