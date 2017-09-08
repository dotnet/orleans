
using System;
using Orleans.Runtime;

namespace Orleans.TestingHost.Utils
{
    /// <summary>
    /// Test logger that does nothing with the logs.
    /// </summary>
    public class NoOpTestLogger : Logger
    {
        /// <summary>
        /// Singleton instance of logger
        /// </summary>
        public static Logger Instance = new NoOpTestLogger();

        /// <inheritdoc />
        public override Severity SeverityLevel => Severity.Off;

        /// <inheritdoc />
        public override string Name => nameof(NoOpTestLogger);

        /// <inheritdoc />
        public override Logger GetLogger(string loggerName)
        {
            return Instance;
        }

        /// <inheritdoc />
        public override void Log(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
        }
    }
}
