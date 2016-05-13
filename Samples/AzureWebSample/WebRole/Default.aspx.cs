using System;
using System.IO;
using HelloEnvironmentInterfaces;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Azure.Samples.Web
{
    public partial class _Default : System.Web.UI.Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (!Page.IsPostBack || AzureClient.IsInitialized)
                return;

            var config = AzureClient.DefaultConfiguration();
            AzureClient.Initialize(config);
        }

        protected async void ButtonSayHello_Click(object sender, EventArgs e)
        {
            this.ReplyText.Text += "\n" + "Talking to Orleans";

            IHelloEnvironment grainRef = GrainClient.GrainFactory.GetGrain<IHelloEnvironment>(0);

            try
            {
                string reply = await grainRef.RequestDetails();
                this.ReplyText.Text += "\n" + "Orleans said: " + reply + " at " + DateTime.UtcNow + " UTC";
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                
                this.ReplyText.Text = "\n" + "Error connecting to Orleans: " + exc + " at " + DateTime.Now;
            }
        }
    }
}
