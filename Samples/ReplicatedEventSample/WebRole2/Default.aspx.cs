using System;
using System.Collections.Generic;
using System.IO;
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

                    CountTextBox.Text = "10";
                }
            }
        }

        protected async void ButtonRefresh_Click(object sender, EventArgs e)
        {
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

        protected async void ButtonStart_Click(object sender, EventArgs e)
        {
            try
            {
                var tasks = new List<Task>();

                int count = int.Parse(CountTextBox.Text);

                for (int i = 0; i < count; i++)
                {
                    var generator = GrainClient.GrainFactory.GetGrain<IGeneratorGrain>(i);
                    tasks.Add(generator.Start());
                }

                await Task.WhenAll(tasks);

                TextBox2.Text = string.Format("Started {0} Generators", count);
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                TextBox2.Text = exc.ToString();
            }
        }
    }
}