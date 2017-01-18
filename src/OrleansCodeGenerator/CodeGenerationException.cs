namespace Orleans.CodeGenerator
{
    using System;
    using System.Runtime.Serialization;

    /// <summary>
    /// The code generation exception.
    /// </summary>
    [Serializable]
    public class CodeGenerationException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CodeGenerationException"/> class.
        /// </summary>
        public CodeGenerationException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CodeGenerationException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        public CodeGenerationException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CodeGenerationException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        public CodeGenerationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

#if !NETSTANDARD
        /// <summary>
        /// Initializes a new instance of the <see cref="CodeGenerationException"/> class.
        /// </summary>
        /// <param name="info">
        /// The info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        protected CodeGenerationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#endif
    }
}
