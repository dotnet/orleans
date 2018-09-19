using System;
using System.Reflection;
using System.Text;

namespace Microsoft.Orleans.CodeGenerator.MSBuild
{
    internal static class LogFormatter
    {
        /// <summary>
        /// Utility function to convert an exception into printable format, including expanding and formatting any nested sub-expressions.
        /// </summary>
        /// <param name="exception">The exception to be printed.</param>
        /// <returns>Formatted string representation of the exception, including expanding and formatting any nested sub-expressions.</returns>
        public static string PrintException(Exception exception)
        {
            return exception == null ? String.Empty : PrintException_Helper(exception, 0, true);
        }

        private static string PrintException_Helper(Exception exception, int level, bool includeStackTrace)
        {
            if (exception == null) return String.Empty;
            var sb = new StringBuilder();
            sb.Append(PrintOneException(exception, level, includeStackTrace));
            if (exception is ReflectionTypeLoadException loadException)
            {
                var loaderExceptions = loadException.LoaderExceptions;
                if (loaderExceptions == null || loaderExceptions.Length == 0)
                {
                    sb.Append("No LoaderExceptions found");
                }
                else
                {
                    foreach (Exception inner in loaderExceptions)
                    {
                        // call recursively on all loader exceptions. Same level for all.
                        sb.Append(PrintException_Helper(inner, level + 1, includeStackTrace));
                    }
                }
            }
            else if (exception is AggregateException)
            {
                var innerExceptions = ((AggregateException)exception).InnerExceptions;
                if (innerExceptions == null) return sb.ToString();

                foreach (Exception inner in innerExceptions)
                {
                    // call recursively on all inner exceptions. Same level for all.
                    sb.Append(PrintException_Helper(inner, level + 1, includeStackTrace));
                }
            }
            else if (exception.InnerException != null)
            {
                // call recursively on a single inner exception.
                sb.Append(PrintException_Helper(exception.InnerException, level + 1, includeStackTrace));
            }
            return sb.ToString();
        }

        private static string PrintOneException(Exception exception, int level, bool includeStackTrace)
        {
            if (exception == null) return String.Empty;
            string stack = String.Empty;
            if (includeStackTrace && exception.StackTrace != null)
                stack = String.Format(Environment.NewLine + exception.StackTrace);

            string message = exception.Message;

            return string.Format(Environment.NewLine + "Exc level {0}: {1}: {2}{3}",
                level,
                exception.GetType(),
                message,
                stack);
        }
    }
}