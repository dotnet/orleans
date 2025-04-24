#if NET6_0_OR_GREATER
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Orleans.Utilities;

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
        /// Gets or sets the type of the original exception.
        /// </summary>
        public string OriginalExceptionType { get; set; }

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
#endif