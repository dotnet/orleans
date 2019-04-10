using System;

namespace Orleans.Runtime.MembershipService
{
    internal interface IFatalErrorHandler
    {
        void OnFatalException(object sender, string context, Exception exception);
    }
}
