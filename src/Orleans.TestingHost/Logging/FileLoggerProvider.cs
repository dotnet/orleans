using System;
using Microsoft.Extensions.Logging;

namespace Orleans.TestingHost.Logging
{
    /// <summary>
    /// FileLoggerProvider implements ILoggerProvider, creates <see cref="FileLogger"/>
    /// </summary>
    public class FileLoggerProvider : ILoggerProvider
    {
        private FileLoggingOutput output;
        public FileLoggerProvider(string filePath)
        {
            this.output = new FileLoggingOutput(filePath);
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new FileLogger(this.output, categoryName);
        }

        public void Dispose()
        {
            this.output.Dispose();
        }
    }

    /// <summary>
    /// Extension methods to configure ILoggingBuilder with FileLoggerProvider
    /// </summary>
    public static class FileLoggerProviderExtensions
    {
        /// <summary>
        /// Add <see cref="FileLoggerProvider"/> to <paramref name="builder"/>
        /// </summary>
        /// <param name="builder">logging builder</param>
        /// <param name="filePathName">log file path</param>
        /// <returns></returns>
        public static ILoggingBuilder AddFile(
            this ILoggingBuilder builder,
            string filePathName)
        {
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));
            builder.AddProvider(new FileLoggerProvider(filePathName));
            return builder;
        }
    }
}
