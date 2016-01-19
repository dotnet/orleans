namespace Orleans.CodeGeneration
{
    using System;
    using System.Reflection;

    /// <summary>
    /// The attribute which informs the code generator that code should be generated an assembly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public class KnownAssemblyAttribute : Attribute
    {
        public KnownAssemblyAttribute(Type type)
        {
            this.Assembly = type.Assembly;
        }

        public KnownAssemblyAttribute(string assemblyName)
        {
            this.Assembly = Assembly.Load(assemblyName);
        }

        /// <summary>
        /// Gets or sets the assembly to include in code generation.
        /// </summary>
        public Assembly Assembly { get; set; }
    }
}
