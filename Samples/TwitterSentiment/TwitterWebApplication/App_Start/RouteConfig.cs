using System.Web.Mvc;
using System.Web.Routing;

namespace TwitterWebApplication
{
    public class RouteConfig
    {
        public static void RegisterRoutes(RouteCollection routes)
        {
            routes.IgnoreRoute("{resource}.axd/{*pathInfo}");

            routes.MapRoute(
                name: "SetScore",
                url: "{hashtags}/{score}",
                defaults: new { controller = "Grain", action = "SetScore" }
            );

            routes.MapRoute(
                name: "GetScore",
                url: "{hashtags}",
                defaults: new { controller = "Grain", action = "GetScores" }
            );

            routes.MapRoute(
                name: "GetIndex",
                url: "",
                defaults: new { controller = "Grain", action = "Index" }
            );


        }
    }
}
