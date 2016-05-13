using System;
using Orleans.Runtime;

namespace Orleans.SqlUtils.StorageProvider
{
    internal static class LoggerExtensions
    {
        public static void Error(this Logger log, string message, Exception exception = null)
        {
            log.Error(0, message, exception);
        }

        public static void Warn(this Logger log, string message, Exception exception)
        {
            log.Warn(0, message, exception);
        }

        public static void Warn(this Logger log, string format, params object[] args)
        {
            log.Warn(0, format, args);
        }
    }
}