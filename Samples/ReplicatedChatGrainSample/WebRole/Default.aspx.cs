/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.IO;
using ReplicatedChatGrainSample.Interfaces;
using Orleans.Runtime.Host;
using System.Threading.Tasks;
using Microsoft.Azure;

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

                    UpdateMessageText();
                }

            }
        }

        private static int sequencecounter = 1;

        private void UpdateMessageText()
        {
            NameTextBox.Text = CloudConfigurationManager.GetSetting("MessagePrefix") + " " + sequencecounter++;
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
                IChatGrainInterface grainRef = GrainClient.GrainFactory.GetGrain<IChatGrainInterface>(0);
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
           var confirmedstate = string.Join("\n", s.ConfirmedState);
           if (this.ConfirmedState.Text != confirmedstate)
               this.ConfirmedState.Text = confirmedstate;
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
                IChatGrainInterface grainRef = GrainClient.GrainFactory.GetGrain<IChatGrainInterface>(0);
                LocalState s = await grainRef.AppendMessage(NameTextBox.Text);
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
                IChatGrainInterface grainRef = GrainClient.GrainFactory.GetGrain<IChatGrainInterface>(0);
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
