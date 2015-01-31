//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

using System;
using System.IO;
using Orleans.Runtime.Host;
using HelloWorldInterfaces;

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

            IHello grainRef = GrainFactory.GetGrain<IHello>(0);

            try
            {
                string msg = await grainRef.SayHello(this.NameTextBox.Text);
                this.ReplyText.Text = "Orleans said: " + msg + " at " + DateTime.UtcNow + " UTC";
            }
            catch (Exception exc)
            {
                while (exc is AggregateException) exc = exc.InnerException;

                this.ReplyText.Text = "Error connecting to Orleans: " + exc + " at " + DateTime.Now;
            }
        }
    }
}
