using Microsoft.Owin;
using Orleans;
using Orleans.Runtime.Configuration;
using Owin;
[assembly: OwinStartup(typeof(AdventureWebClient.Startup))]

namespace AdventureWebClient
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            // Any connection or hub wire up and configuration should go here
            app.MapSignalR();


            // Initialize Orleans
            var config = ClientConfiguration.LocalhostSilo();
            GrainClient.Initialize(config);
        }

    }
}
