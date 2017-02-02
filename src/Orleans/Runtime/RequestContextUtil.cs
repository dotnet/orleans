using System.Collections.Generic;

namespace Orleans.Runtime
{
    public class RequestContextUtil
    {
        public Dictionary<string, object> Export()
        {
            return RequestContext.Export();
        }
    }
}