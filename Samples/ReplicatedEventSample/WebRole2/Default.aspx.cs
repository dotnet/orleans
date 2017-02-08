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
using ReplicatedEventSample.Interfaces;
using Orleans.Runtime.Host;
using System.Threading.Tasks;
using Microsoft.Azure;
using System.Text;
using System.Collections.Generic;

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
                ITickerGrain tickergrain = GrainClient.GrainFactory.GetGrain<ITickerGrain>(0);
                TextBox1.Text = await tickergrain.GetTickerLine();
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                this.TextBox1.Text = exc.ToString();
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

                this.TextBox2.Text = string.Format("Started {0} Generators", count);
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;
                this.TextBox2.Text = exc.ToString();
            }
        }

    }
}
