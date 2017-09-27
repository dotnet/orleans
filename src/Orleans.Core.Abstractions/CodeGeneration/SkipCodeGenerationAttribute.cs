namespace Orleans.CodeGeneration
{
    using System;

    /// <summary>
    /// The attribute which informs the code generator that no code should be generated an assembly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public class SkipCodeGenerationAttribute : Attribute
    {
    }
}
