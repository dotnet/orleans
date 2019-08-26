using System;

namespace Orleans.Runtime
{
    internal interface IFatalErrorHandler
    {
        bool IsUnexpected(Exception exception);
        void OnFatalException(object sender = null, string context = null, Exception exception = null);
    }
}
