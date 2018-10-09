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
        /// <summary>Initializes a new instance of <see cref="KnownAssemblyAttribute"/>.</summary>
        /// <param name="type">A type contained by the target assembly. 
        /// The type itself is not relevant, and it's just a way to indirectly identify the assembly.</param>
        public KnownAssemblyAttribute(Type type)
        {
            this.Assembly = type.GetTypeInfo().Assembly;
        }

        /// <summary>
        /// Gets or sets the assembly to include in code generation.
        /// </summary>
        public Assembly Assembly { get; }

        /// <summary>
        /// Gets or sets a value indicating whether or not to assume that all types in the specified assembly are
        /// serializable.
        /// </summary>
        /// <remarks>This is equivalent to specifying <see cref="KnownTypeAttribute"/> for all types.</remarks>
        public bool TreatTypesAsSerializable { get; set; }
    }
}
