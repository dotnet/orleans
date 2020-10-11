using System.Diagnostics;

namespace Orleans.CodeGenerator
{
    public class CodeGeneratorOptions
    {
        /// <summary>
        /// Whether or not to add <see cref="DebuggerStepThroughAttribute"/> to generated code.
        /// </summary>
        public bool DebuggerStepThrough { get; set; }
    }
}