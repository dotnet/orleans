namespace Orleans.CodeGeneration
{
    using System;

    /// <summary>
    /// The attribute which informs the code generator which assemblies an assembly contains generated code for.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class OrleansCodeGenerationTargetAttribute : Attribute
    {
        /// <summary>Initializes a new instance of <see cref="OrleansCodeGenerationTargetAttribute"/>.</summary>
        /// <param name="assemblyName">The target assembly name.</param>
        public OrleansCodeGenerationTargetAttribute(string assemblyName)
        {
            this.AssemblyName = assemblyName;
        }

        /// <summary>
        /// The target assembly name that the generated code is for.
        /// </summary>
        public string AssemblyName { get; set; }
    }
}
