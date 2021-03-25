using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    public interface IFatalErrorHandler
    {
        bool IsUnexpected(Exception exception);
        ValueTask OnFatalException(object sender = null, string context = null, Exception exception = null);
    }
}
