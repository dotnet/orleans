using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans;
using Orleans.Runtime.Host;
using System.Web.Mvc;
using System.Web.Routing;

namespace GPSTracker.Web
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            if (RoleEnvironment.IsAvailable)
            {
                // running in Azure
                AzureClient.Initialize(Server.MapPath(@"~/AzureConfiguration.xml"));
            }
            else
            {
                // not running in Azure
                GrainClient.Initialize(Server.MapPath(@"~/LocalConfiguration.xml"));
            }
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
        }
    }
}
