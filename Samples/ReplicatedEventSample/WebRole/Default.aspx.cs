using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.UI;
using Orleans.Runtime.Host;
using ReplicatedEventSample.Interfaces;

namespace Orleans.Azure.Samples.Web
{
    public partial class _Default : Page
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
                        throw new FileNotFoundException(
                            string.Format("Cannot find Orleans client config file for initialization at {0}",
                                clientConfigFile.FullName), clientConfigFile.FullName);
                    }
                    AzureClient.Initialize(clientConfigFile);

                    NameTextBox.Text = "event0";
                }
            }
        }

        protected async void ButtonRefresh_Click(object sender, EventArgs e)
        {
            TextBox1.Text = "";
            await RefreshTickerLine();
        }

        protected async void Timer1_Tick(object sender, EventArgs e)
        {
            await RefreshTickerLine();
        }

        private async Task RefreshTickerLine()
        {
            try
            {
                // get ticker line
                ITickerGrain tickerGrain = GrainClient.GrainFactory.GetGrain<ITickerGrain>(0);
                TextBox1.Text = await tickerGrain.GetTickerLine();
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                TextBox1.Text = exc.ToString();
            }
        }


        protected async void ButtonLookup_Click(object sender, EventArgs e)
        {
            TextBox2.Text = "";
            try
            {
                IEventGrain grainRef = GrainClient.GrainFactory.GetGrain<IEventGrain>(NameTextBox.Text);
                var topThree = await grainRef.GetTopThree();
                var s = new StringBuilder();
                if (topThree.Count > 0)
                    s.AppendLine("First Place: " + topThree[0]);
                if (topThree.Count > 1) // if this were real we would check for ties
                    s.AppendLine("Second Place: " + topThree[1]);
                if (topThree.Count > 2)
                    s.AppendLine("Third Place: " + topThree[2]);

                TextBox2.Text = s.ToString();
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                TextBox2.Text = exc.ToString();
            }
        }
    }
}