using System;

namespace Orleans.Runtime
{
    internal interface IFatalErrorHandler
    {
        void OnFatalException(object sender = null, string context = null, Exception exception = null);
    }
}
