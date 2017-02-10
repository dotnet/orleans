using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure;
using Orleans.Runtime.Host;
using ReplicatedChatGrainSample.Interfaces;

namespace Orleans.Azure.Samples.Web
{
    public partial class _Default : System.Web.UI.Page
    {
        private static int sequencecounter = 1;

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
                    AzureClient.Initialize(clientConfigFile);

                    UpdateMessageText();
                }
            }
        }

        private void UpdateMessageText()
        {
            this.NameTextBox.Text = CloudConfigurationManager.GetSetting("MessagePrefix") + " " + sequencecounter++;
        }

        protected async void ButtonRefresh_Click(object sender, EventArgs e)
        {
            await Refresh();
        }

        protected async void Timer1_Tick(object sender, EventArgs e)
        {
            await Refresh();
        }

        private async Task Refresh()
        {
            try
            {
                IChatGrain grainRef = GrainClient.GrainFactory.GetGrain<IChatGrain>(0);
                LocalState s = await grainRef.GetLocalState();
                UpdateText(s);
            }
            catch (Exception exc)
            {
                DisplayError(exc);
            }
        }

        private void UpdateText(LocalState s)
        {
            var confirmedStateText = string.Join("\n", s.ConfirmedState);
            if (this.ConfirmedState.Text != confirmedStateText)
                this.ConfirmedState.Text = confirmedStateText;
            var unconfirmedupdates = string.Join("\n", s.UnconfirmedEvents);
            if (this.UnconfirmedUpdates.Text != unconfirmedupdates)
                this.UnconfirmedUpdates.Text = unconfirmedupdates;
            var tentativestate = string.Join("\n", s.TentativeState);
            if (this.TentativeState.Text != tentativestate)
                this.TentativeState.Text = tentativestate;
        }

        private void DisplayError(Exception exc)
        {
            while (exc is AggregateException) exc = exc.InnerException;

            this.TentativeState.Text = "Error connecting to Orleans: " + exc + " at " + DateTime.Now;
            this.ConfirmedState.Text = "";
            this.UnconfirmedUpdates.Text = "";
        }

        protected async void ButtonAppendMessage_Click(object sender, EventArgs e)
        {
            try
            {
                IChatGrain grainRef = GrainClient.GrainFactory.GetGrain<IChatGrain>(0);
                LocalState s = await grainRef.AppendMessage(this.NameTextBox.Text);
                UpdateText(s);
                UpdateMessageText();
            }
            catch (Exception exc)
            {
                DisplayError(exc);
            }
        }

        protected async void ButtonClearAll_Click(object sender, EventArgs e)
        {
            try
            {
                IChatGrain grainRef = GrainClient.GrainFactory.GetGrain<IChatGrain>(0);
                LocalState s = await grainRef.ClearAll();
                UpdateText(s);
            }
            catch (Exception exc)
            {
                DisplayError(exc);
            }
        }
    }
}