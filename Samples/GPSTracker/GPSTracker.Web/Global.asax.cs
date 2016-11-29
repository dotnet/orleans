using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans;
using Orleans.Runtime.Host;
using System.Web.Mvc;
using System.Web.Routing;
using GPSTracker.Common;
using Orleans.Runtime.Configuration;

namespace GPSTracker.Web
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            

            if (AzureEnvironment.IsInAzure)
            {
                // running in Azure
                var config = AzureClient.DefaultConfiguration();
                AzureClient.Initialize(config);
            }
            else
            {
                // not running in Azure
                var config = ClientConfiguration.LocalhostSilo();
                GrainClient.Initialize(config);
            }

            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
        }
    }
}
