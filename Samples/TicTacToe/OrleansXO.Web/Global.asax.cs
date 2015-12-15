using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans;
using Orleans.Runtime.Host;
using System.Web.Mvc;
using System.Web.Routing;

namespace OrleansXO.Web
{
    // Note: For instructions on enabling IIS7 classic mode, 
    // visit http://go.microsoft.com/fwlink/?LinkId=301868
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
            RouteConfig.RegisterRoutes(RouteTable.Routes);
        }
    }
}
