namespace Orleans.CodeGenerator
{
    using System.Collections.Generic;
    using System.Reflection;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    /// <summary>
    /// Represents generated code syntax.
    /// </summary>
    internal class GeneratedSyntax
    {
        /// <summary>
        /// Gets or sets the collection of assemblies which this syntax was generated for.
        /// </summary>
        public List<Assembly> SourceAssemblies { get; set; }

        /// <summary>
        /// Gets or sets generated abstract syntax tree for the source assemblies.
        /// </summary>
        public CompilationUnitSyntax Syntax { get; set; }
    }
}
