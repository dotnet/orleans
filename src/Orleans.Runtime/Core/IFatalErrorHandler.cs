using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Interface for controlling how fatal errors (such as a silo being declared defunct) are handled.
    /// </summary>
    public interface IFatalErrorHandler
    {
        /// <summary>
        /// Determines whether the specified exception is unexpected.
        /// </summary>
        /// <param name="exception">The exception.</param>
        /// <returns><see langword="true"/> if the specified exception is unexpected; otherwise, <see langword="false"/>.</returns>
        bool IsUnexpected(Exception exception);

        /// <summary>
        /// Called when a fatal exception occurs.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="context">The context.</param>
        /// <param name="exception">The exception.</param>
        void OnFatalException(object sender = null, string context = null, Exception exception = null);
    }
}
