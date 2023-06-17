using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime
{
    /// <summary>
    /// An exception class used by the Orleans runtime for reporting errors.
    /// </summary>
    /// <remarks>
    /// This is also the base class for any more specific exceptions 
    /// raised by the Orleans runtime.
    /// </remarks>
    [Serializable]
    [GenerateSerializer]
    public class WrappedException : OrleansException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="WrappedException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        public WrappedException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WrappedException"/> class.
        /// </summary>
        /// <param name="info">
        /// The serialization info.
        /// </param>
        /// <param name="context">
        /// The context.
        /// </param>
        /// <exception cref="SerializationException">The class name is <see langword="null" /> or <see cref="P:System.Exception.HResult" /> is zero (0).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="info" /> is <see langword="null" />.</exception>
        protected WrappedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            OriginalExceptionType = info.GetString(nameof(OriginalExceptionType));
        }

        /// <summary>
        /// Gets or sets the type of the original exception.
        /// </summary>
        [Id(0)]
        public string OriginalExceptionType { get; set; }

        /// <inheritdoc/>
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(OriginalExceptionType), OriginalExceptionType);
        }

        /// <summary>
        /// Creates a new instance of the <see cref="WrappedException"/> class and rethrows it using the provided exception's stack trace.
        /// </summary>
        /// <param name="exception">The exception.</param>
        [DoesNotReturn]
        public static void CreateAndRethrow(Exception exception)
        {
            var error = exception switch
            {
                WrappedException => exception,
                { } => CreateFromException(exception),
                null => throw new ArgumentNullException(nameof(exception))
            };

            ExceptionDispatchInfo.Throw(error);
        }

        private static WrappedException CreateFromException(Exception exception)
        {
            var originalExceptionType = RuntimeTypeNameFormatter.Format(exception.GetType());
            var detailedMessage = LogFormatter.PrintException(exception);
            var result = new WrappedException(detailedMessage)
            {
                OriginalExceptionType = originalExceptionType,
            };

            ExceptionDispatchInfo.SetRemoteStackTrace(result, exception.StackTrace);
            return result;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"{nameof(WrappedException)} OriginalType: {OriginalExceptionType}, Message: {Message}";
        }
    }
}