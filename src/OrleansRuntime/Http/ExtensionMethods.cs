using Microsoft.Owin;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal static class ExtensionMethods
    {
        public static Task ReturnJson(this IOwinContext context, object value)
        {
            context.Response.ContentType = "application/json";
            if (null == value)
            {
                return TaskDone.Done;
            }
            return context.Response.WriteAsync(JsonConvert.SerializeObject(value));
        }

        public static Task ReturnError(this IOwinContext context, Exception ex)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync(ex.ToString());
        }

        public static Task ReturnUnauthorised(this IOwinContext context)
        {
            context.Response.StatusCode = 401;
            context.Response.ReasonPhrase = "Unauthorized";
            context.Response.Headers.Add("WWW-Authenticate", new string[] { "Basic realm=\"OrleansHttp\"" });
            return Task.FromResult(0);
        }

        public static void ReturnNotFound(this IOwinContext context)
        {
            context.Response.StatusCode = 404;
            context.Response.ReasonPhrase = "Not Found";
        }
    }
}
