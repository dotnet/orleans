namespace Orleans.CodeGeneration
{
    using System;
    using System.Reflection;

    /// <summary>
    /// The attribute which informs the code generator that code should be generated an assembly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class KnownAssemblyAttribute : Attribute
    {
        public KnownAssemblyAttribute(Type type)
        {
            this.Assembly = type.GetTypeInfo().Assembly;
        }

        public KnownAssemblyAttribute(string assemblyName)
        {
            this.Assembly = Assembly.Load(new AssemblyName(assemblyName));
        }

        /// <summary>
        /// Gets or sets the assembly to include in code generation.
        /// </summary>
        public Assembly Assembly { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether or not to assume that all types in the specified assembly are
        /// serializable.
        /// </summary>
        /// <remarks>This is equivalent to specifying <see cref="KnownTypeAttribute"/> for all types.</remarks>
        public bool TreatTypesAsSerializable { get; set; }
    }
}
