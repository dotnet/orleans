using System;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Orleans.Runtime.MembershipService
{
    internal class FatalErrorHandler : IFatalErrorHandler
    {
        private readonly ILogger<FatalErrorHandler> log;

        public FatalErrorHandler(ILogger<FatalErrorHandler> log)
        {
            this.log = log;
        }

        public void OnFatalException(object sender, string context, Exception exception)
        {
            var msg = $"FATAL EXCEPTION from {sender?.ToString() ?? "null"}. Context: {context}. Exception: {LogFormatter.PrintException(exception)}.\nCurrent stack: {Environment.StackTrace}";
            this.log.LogError((int)ErrorCode.Logger_ProcessCrashing, msg);


            // TODO: Should we initiate shutdown instead? might be worth having two methods, one for failfast and one for shutdown? Hard to reason about shutdown in these cases...
            // Can also signal shutdown using IApplicationLifetime, perhaps.

            Debugger.Break();
            Environment.FailFast(msg);
        }
    }
}
