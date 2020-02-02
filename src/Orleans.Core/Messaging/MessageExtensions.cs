namespace Orleans.Runtime
{
    internal static class MessageExtensions
    {
        public static bool IsPing(this Message msg)
        {
            var requestContext = msg.RequestContextData;
            if (requestContext != null &&
                requestContext.TryGetValue(RequestContext.PING_APPLICATION_HEADER, out var pingObj) &&
                pingObj is bool isPing
                && isPing)
            {
                return true;
            }

            return false;
        }
    }
}
