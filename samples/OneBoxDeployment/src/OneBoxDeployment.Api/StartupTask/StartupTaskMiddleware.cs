using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace OneBoxDeployment.Api.StartupTask
{
    /// <summary>
    /// Little piece of middleware to make starting asynchronous startup tasks easier.
    /// </summary>
    /// <remarks>Based on code by Andrew Lock at https://andrewlock.net/running-async-tasks-on-app-startup-in-asp-net-core-part-4-using-health-checks/.
    /// See some problems and improvements
    /// <ul>
    ///     <li>https://tools.ietf.org/html/draft-inadarei-api-health-check-02</li>
    ///     <li>https://github.com/aspnet/AspNetCore/issues/5936</li>
    /// </ul>
    /// </remarks>
    public class StartupTasksMiddleware
    {
        private readonly StartupTaskContext _context;
        private readonly RequestDelegate _next;

        public StartupTasksMiddleware(StartupTaskContext context, RequestDelegate next)
        {
            _context = context;
            _next = next;
        }

        public async Task Invoke(HttpContext httpContext)
        {
            if(_context.IsComplete)
            {
                await _next(httpContext);
            }
            else
            {
                var response = httpContext.Response;
                response.StatusCode = 503;
                response.Headers["Retry-After"] = "30";
                await response.WriteAsync("Service Unavailable");
            }
        }
    }
}
