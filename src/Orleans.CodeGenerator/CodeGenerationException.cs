using System;
using System.Runtime.Serialization;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Signifies an error that occurred during code generation.
    /// </summary>
    [Serializable]
    public class CodeGenerationException : Exception
    {
        public CodeGenerationException()
        {
        }

        public CodeGenerationException(string message)
            : base(message)
        {
        }

        public CodeGenerationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        protected CodeGenerationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}