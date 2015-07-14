using GPSTracker.Common;
using Microsoft.AspNet.SignalR;

namespace GPSTracker.Web.Controllers
{
    public class LocationHub : Hub
    {
        public void LocationUpdate(VelocityMessage message)
        {
            // Forward a single messages to all browsers
            Clients.Group("BROWSERS").locationUpdate(message);
        }

        public void LocationUpdates(VelocityBatch messages)
        {
            // Forward a batch of messages to all browsers
            Clients.Group("BROWSERS").locationUpdates(messages);
        }

        public override System.Threading.Tasks.Task OnConnected()
        {
            if (Context.Headers.Get("ORLEANS") != "GRAIN")
            {
                // This connection does not have the GRAIN header, so it must be a browser
                // Therefore add this connection to the browser group
                Groups.Add(Context.ConnectionId, "BROWSERS");
            }
            return base.OnConnected();
        }


    }
}
