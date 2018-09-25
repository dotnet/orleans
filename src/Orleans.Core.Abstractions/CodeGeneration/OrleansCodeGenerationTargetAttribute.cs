namespace Orleans.CodeGeneration
{
    using System;

    /// <summary>
    /// The attribute which informs the code generator which assemblies an assembly contains generated code for.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class OrleansCodeGenerationTargetAttribute : Attribute
    {
        /// <summary>Initializes a new instance of <see cref="OrleansCodeGenerationTargetAttribute"/>.</summary>
        /// <param name="assemblyName">The target assembly name.</param>
        public OrleansCodeGenerationTargetAttribute(string assemblyName)
        {
            this.AssemblyName = assemblyName;
        }

        /// <summary>Initializes a new instance of <see cref="OrleansCodeGenerationTargetAttribute"/>.</summary>
        /// <param name="assemblyName">The target assembly name.</param>
        /// <param name="metadataOnly">Whether or not only metadata was generated for the target assembly</param>
        public OrleansCodeGenerationTargetAttribute(string assemblyName, bool metadataOnly)
        {
            this.AssemblyName = assemblyName;
            this.MetadataOnly = metadataOnly;
        }

        /// <summary>
        /// The target assembly name that the generated code is for.
        /// </summary>
        public string AssemblyName { get; }

        /// <summary>
        /// Whether or not only metadata was generated for the target assembly.
        /// </summary>
        public bool MetadataOnly { get; }
    }
}
