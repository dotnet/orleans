using Microsoft.Owin;
using Owin;

[assembly: OwinStartupAttribute(typeof(GPSTracker.Web.Startup))]
namespace GPSTracker.Web
{
    public partial class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            app.MapSignalR();
        }
    }
}
