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
using HelloGeoInterfaces;
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
                    try
                    {
                        AzureClient.Initialize(clientConfigFile);
                    }
                    catch (Exception exc)
                    {
                        this.ReplyText.Text = "Error initializing Orleans Client: " + exc + " at " + DateTime.UtcNow + " UTC";

                    }
                }
            }
        }      
      
        protected async void ButtonSayHello_Click(object sender, EventArgs e)
        {
            var targetgrain = OipcGrain.Text;

            if (string.IsNullOrEmpty(targetgrain))
            {
                this.ReplyText.Text = "Please enter a key";
                return;
            }

            IHelloGrain grainRef = GrainClient.GrainFactory.GetGrain<IHelloGrain>(targetgrain, "HelloGeoGrains.OneInstancePerClusterGrain");

            try
            {   
                string reply = await grainRef.Ping();
                this.ReplyText.Text = "OneInstancePerCluster-Grain \"" + targetgrain + "\" answered: " + reply + "\n\n at " + DateTime.UtcNow + " UTC";
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;

                this.ReplyText.Text = "Error connecting to Orleans: " + exc + " at " + DateTime.UtcNow + " UTC";
            }
        }

        protected async void ButtonSayHelloSingleInstance_Click(object sender, EventArgs e)
        {
            var targetgrain = GsiGrain.Text;

            if (string.IsNullOrEmpty(targetgrain))
            {
                this.ReplyText.Text = "Please enter a key";
                return;
            }

            IHelloGrain grainRef = GrainClient.GrainFactory.GetGrain<IHelloGrain>(targetgrain, "HelloGeoGrains.GlobalSingleInstanceGrain");

            try
            {
                string reply = await grainRef.Ping();
                this.ReplyText.Text = "GlobalSingleInstance-Grain \"" + targetgrain + "\" answered: " + reply + "\n\n at " + DateTime.UtcNow + " UTC";
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;

                this.ReplyText.Text = "Error connecting to Orleans: " + exc + " at " + DateTime.UtcNow + " UTC";
            }
        }
    }
}
