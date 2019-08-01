using System;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using System.Threading;

namespace Orleans.Runtime
{
    internal class FatalErrorHandler : IFatalErrorHandler
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
            if (exception != null)
            {
                this.log.LogError(
                    (int)ErrorCode.Logger_ProcessCrashing,
                    exception,
                    "Fatal error from {Sender}. Context: {Context}. Exception: {Exception}",
                    sender,
                    context,
                    exception);
            }
            else
            {
                this.log.LogError(
                    (int)ErrorCode.Logger_ProcessCrashing,
                    "Fatal error from {Sender}. Context: {Context}",
                    sender,
                    context);
            }

            Debugger.Break();
            if (!this.clusterMembershipOptions.IsRunningAsUnitTest)
            {
                var msg = $"FATAL EXCEPTION from {sender?.ToString() ?? "null"}. Context: {context ?? "null"}. "
                    + $"Exception: {(exception != null ? LogFormatter.PrintException(exception) : "null")}.\n"
                    + $"Current stack: {Environment.StackTrace}";

                Console.Error.WriteLine(msg);

                // Allow some time for loggers to flush.
                Thread.Sleep(2000);

                Environment.FailFast(msg);
            }
            else
            {
                this.log.LogWarning(
                    $"{nameof(ClusterMembershipOptions)}.{nameof(ClusterMembershipOptions.IsRunningAsUnitTest)} is set."
                    + " The process will not be terminated.");
            }
        }
    }
}
