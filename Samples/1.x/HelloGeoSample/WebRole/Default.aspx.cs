using System;
using System.IO;
using HelloGeoInterfaces;
using Orleans.Runtime.Host;

namespace Orleans.Azure.Samples.Web
{
    public partial class _Default : System.Web.UI.Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (this.Page.IsPostBack)
            {
                if (!AzureClient.IsInitialized)
                {
                    FileInfo clientConfigFile = AzureConfigUtils.ClientConfigFileLocation;
                    if (!clientConfigFile.Exists)
                    {
                        throw new FileNotFoundException(
                            string.Format("Cannot find Orleans client config file for initialization at {0}",
                                clientConfigFile.FullName), clientConfigFile.FullName);
                    }
                    try
                    {
                        AzureClient.Initialize(clientConfigFile);
                    }
                    catch (Exception exc)
                    {
                        this.ReplyText.Text = "Error initializing Orleans Client: " + exc + " at " + DateTime.UtcNow +
                                              " UTC";
                    }
                }
            }
        }

        protected async void ButtonSayHello_Click(object sender, EventArgs e)
        {
            var targetGrainKey = this.OipcGrain.Text;

            if (string.IsNullOrEmpty(targetGrainKey))
            {
                this.ReplyText.Text = "Please enter a key";
                return;
            }

            IHelloGrain grainRef = GrainClient.GrainFactory.GetGrain<IHelloGrain>(targetGrainKey,
                "HelloGeoGrains.OneInstancePerClusterGrain");

            try
            {
                string reply = await grainRef.Ping();
                this.ReplyText.Text = string.Format("OneInstancePerCluster-Grain \"{0}\" answered: {1}\n\n at {2} UTC", targetGrainKey, reply, DateTime.UtcNow);
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;

                this.ReplyText.Text = "Error connecting to Orleans: " + exc + " at " + DateTime.UtcNow + " UTC";
            }
        }

        protected async void ButtonSayHelloSingleInstance_Click(object sender, EventArgs e)
        {
            var targetGrainKey = this.GsiGrain.Text;

            if (string.IsNullOrEmpty(targetGrainKey))
            {
                this.ReplyText.Text = "Please enter a key";
                return;
            }

            IHelloGrain grainRef = GrainClient.GrainFactory.GetGrain<IHelloGrain>(targetGrainKey,
                "HelloGeoGrains.GlobalSingleInstanceGrain");

            try
            {
                string reply = await grainRef.Ping();
                this.ReplyText.Text = string.Format("GlobalSingleInstance-Grain \"{0}\" answered: {1}\n\n at {2} UTC", targetGrainKey, reply, DateTime.UtcNow);
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;

                this.ReplyText.Text = "Error connecting to Orleans: " + exc + " at " + DateTime.UtcNow + " UTC";
            }
        }
    }
}