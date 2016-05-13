namespace Orleans.CodeGeneration
{
    using System.Reflection;

    /// <summary>
    /// Methods for generating source code.
    /// </summary>
    internal interface ISourceCodeGenerator
    {
        /// <summary>
        /// Generates source code for the provided assembly.
        /// </summary>
        /// <param name="input">
        /// The assembly to generate source for.
        /// </param>
        /// <returns>
        /// The generated source.
        /// </returns>
        string GenerateSourceForAssembly(Assembly input);
    }
}
