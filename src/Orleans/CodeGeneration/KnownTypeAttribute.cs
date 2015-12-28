namespace Orleans.CodeGeneration
{
    using System;

    /// <summary>
    /// The attribute which informs the code generator that code should be generated a type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public class KnownTypeAttribute : Attribute
    {
        public KnownTypeAttribute(Type type)
        {
            this.Type = type;
        }

        public Type Type { get; set; }
    }
}
