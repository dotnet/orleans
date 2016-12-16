using Microsoft.Owin;
using Owin;
using System;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Runtime
{

    /// <summary>
    /// A general purpose web server built on Owin which routes requests based on the supplied router
    /// </summary>
    internal class WebServer 
    {
        readonly Router router;
        readonly string username;
        readonly string password;

        public WebServer(Router router, string username, string password)
        {
            this.router = router;
            this.username = username;
            this.password = password;
        }

        async Task HandleRequest(IOwinContext context, Func<Task> func)
        {
            var result = this.router.Match(context.Request.Path.Value);
            if (null != result)
            {
                try
                {
                    await result(context);
                    return;
                }
                catch (Exception ex)
                {
                    await context.ReturnError(ex);
                }
            }

            context.ReturnNotFound();
        }

        Task BasicAuth(IOwinContext context, Func<Task> func)
        {
            if (!context.Request.Headers.ContainsKey("Authorization")) return context.ReturnUnauthorised();

            // "Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ=="
            var value = context.Request.Headers["Authorization"];

            var decodedString = Encoding.UTF8.GetString(Convert.FromBase64String(value.Replace("Basic", "").Trim()));

            var parts = decodedString.Split(':');

            if (parts.Length != 2) return context.ReturnUnauthorised();

            if (parts[0] != this.username && parts[1] != this.password) return context.ReturnUnauthorised();

            return func();
        }

        public void Configure(IAppBuilder app)
        {
            if (!string.IsNullOrWhiteSpace(this.username) && !string.IsNullOrWhiteSpace(this.password))
            {
                // if a username and password are supplied, enable basic auth
                app.Use(BasicAuth);
            }
            app.Use(HandleRequest);
        }
    }

}
