using System;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using System.Threading;

namespace Orleans.Runtime
{
    internal partial class FatalErrorHandler : IFatalErrorHandler
    {
        private readonly ILogger<FatalErrorHandler> log;
        private readonly ClusterMembershipOptions clusterMembershipOptions;

        public FatalErrorHandler(
            ILogger<FatalErrorHandler> log,
            IOptions<ClusterMembershipOptions> clusterMembershipOptions)
        {
            this.log = log;
            this.clusterMembershipOptions = clusterMembershipOptions.Value;
        }

        public bool IsUnexpected(Exception exception)
        {
            return !(exception is ThreadAbortException);
        }

        public void OnFatalException(object sender, string context, Exception exception)
        {
            LogFatalError(this.log, exception, sender, context);

            var msg = @$"FATAL EXCEPTION from {sender?.ToString() ?? "null"}. Context: {context ?? "null"
                }. Exception: {(exception != null ? LogFormatter.PrintException(exception) : "null")}.\nCurrent stack: {Environment.StackTrace}";
            Console.Error.WriteLine(msg);

            // Allow some time for loggers to flush.
            Thread.Sleep(2000);

            if (Debugger.IsAttached) Debugger.Break();

            Environment.FailFast(msg, exception);
        }

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Logger_ProcessCrashing,
            Message = "Fatal error from {Sender}. Context: {Context}"
        )]
        private static partial void LogFatalError(ILogger logger, Exception exception, object sender, string context);
    }
}
