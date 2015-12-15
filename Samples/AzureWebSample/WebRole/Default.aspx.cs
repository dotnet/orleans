using System;
using System.IO;
using HelloEnvironmentInterfaces;
using Orleans.Runtime.Host;

namespace Orleans.Azure.Samples.Web
{
    public partial class _Default : System.Web.UI.Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (Page.IsPostBack)
            {
                if (!AzureClient.IsInitialized)
                {
                    FileInfo clientConfigFile = AzureConfigUtils.ClientConfigFileLocation;
                    if (!clientConfigFile.Exists)
                    {
                        throw new FileNotFoundException(string.Format("Cannot find Orleans client config file for initialization at {0}", clientConfigFile.FullName), clientConfigFile.FullName);
                    }

                    AzureClient.Initialize(clientConfigFile);
                }
            }
        }

        protected async void ButtonSayHello_Click(object sender, EventArgs e)
        {
            this.ReplyText.Text = "Talking to Orleans";

            IHelloEnvironment grainRef = GrainClient.GrainFactory.GetGrain<IHelloEnvironment>(0);

            try
            {
                string reply = await grainRef.RequestDetails();
                this.ReplyText.Text = "Orleans said: " + reply + " at " + DateTime.UtcNow + " UTC";
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                
                this.ReplyText.Text = "Error connecting to Orleans: " + exc + " at " + DateTime.Now;
            }
        }
    }
}
