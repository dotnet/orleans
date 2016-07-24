namespace Orleans.CodeGeneration
{
    using System;

    /// <summary>
    /// The attribute which informs the code generator which assemblies an assembly contains generated code for.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class OrleansCodeGenerationTargetAttribute : Attribute
    {
        public OrleansCodeGenerationTargetAttribute(string assemblyName)
        {
            this.AssemblyName = assemblyName;
        }

        public string AssemblyName { get; set; }
    }
}
