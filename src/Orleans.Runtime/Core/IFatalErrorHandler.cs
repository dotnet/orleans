using System;

namespace Orleans.Runtime
{
    internal interface IFatalErrorHandler
    {
        void OnFatalException(object sender, string context, Exception exception);
    }
}
