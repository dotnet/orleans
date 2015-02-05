using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using ManagementAPI;
using Microsoft.Marlowe.IntelligentPower.MarloweApp;

namespace NodeManager
{

    public partial class Main : Form
    {
        internal static EventWaitHandle ewait = new EventWaitHandle(false, EventResetMode.AutoReset);
        internal int line = 1;

        delegate void SetTextCallback(string text);

        private Thread executeThread= null;
        private bool runTest = true;
        public Main()
        {
            InitializeComponent();
        }
        /// <summary>
        /// method to validate an IP address
        /// using regular expressions. The pattern
        /// being used will validate an ip address
        /// with the range of 1.0.0.0 to 255.255.255.255
        /// </summary>
        /// <param name="addr">Address to validate</param>
        /// <returns>
        ///      <c>true</c> if [is valid IP] [the specified addr]; otherwise, <c>false</c>.
        /// </returns>
        private static bool IsValidIP(string addr)
        {
            ////create our match pattern
            ////string pattern = @"^([1-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])(\.([0-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])){3}$";
            string pattern = @"\b(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b";
            ////create our Regular Expression object
            Regex check = new Regex(pattern);
            ////boolean variable to hold the status
            bool valid = false;
            ////check to make sure an ip address was provided
            if (addr == " ")
            {
                ////no address provided so return false
                valid = false;
            }
            else
            {
                ////address provided so use the IsMatch Method
                ////of the Regular Expression object
                valid = check.IsMatch(addr, 0);
            }
            ////return the results
            return valid;
        }

        private void BtnSend_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(this.txtOrleanPort.Text))
            {
                this.txtStatus.AppendText("Invalid Port");
                this.txtOrleanPort.Focus();
                return;
            }

            try
            {
                if (int.Parse(txtOrleanPort.Text) == 0)
                {
                    this.txtStatus.AppendText("Invalid Port");
                    this.txtOrleanPort.Focus();
                    return;
                }
                
            }
            catch
            {
                this.txtStatus.AppendText("Invalid Port");
                this.txtOrleanPort.Focus();
                return;
            }

            if (string.IsNullOrEmpty(this.txtOrleanServer.Text))
            {
                this.txtStatus.AppendText("Invalid Server");
                this.txtOrleanServer.Focus();
                return;
            }

            if (string.IsNullOrEmpty(this.txtWait.Text) )
            {
                this.txtStatus.AppendText("Invalid Wait Time");
                this.txtOrleanServer.Focus();
                return;
            }
            try
            {
                if (int.Parse(txtWait.Text) == 0)
                {
                    this.txtStatus.AppendText("Invalid Wait Time");
                    this.txtWait.Focus();
                    return;

                }
            }
            catch 
            { 
                this.txtStatus.AppendText("Invalid Wait Time");
                this.txtWait.Focus();
                return;
            }


            
            
           // List< WCFOrleansRuntimeHostInfo> marloweApp.GetOrleansData(txtOrleanServer .Text,txtOrleanPort.Text );
            if (BtnSend.Text == "Start")
            {
                runTest = true;
                BtnSend.Text = "Stop";
                this.executeThread = new Thread(new ThreadStart(this.ExecuteTest ));
                this.executeThread.Start();

            }
            else if (BtnSend.Text == "Stop")
            {
                runTest = false;
                BtnSend.Text = "Start";
            }
           
            
        }

        public void waitEventCallback()
        {
            ewait.Set();
        }

        private void SetText(string text)
        {
            this.txtStatus.AppendText(text);
        }

        private void ExecuteTest()
        {
            string errstring = string.Empty ;
            MarloweApp marloweApp = new MarloweApp();
            marloweApp.m_DelegateSetWaitEventCallback = new MarloweApp.DelegateSetWaitEventCallback(waitEventCallback);
            WCFManagement wcfMgt = new WCFManagement(txtOrleanServer.Text, txtOrleanPort.Text);
            List<WCFOrleansRuntimeHostInfo> activeHost = wcfMgt.WCFManagement_GetActiveHost();
            List<WCFOrleansRuntimeHostInfo> allHost = wcfMgt.WCFManagement_GetAllHost();
            List<WCFOrleansRuntimeHostInfo> suspendedHost = wcfMgt.WCFManagement_GetSuspendedHost();
            //this.txtStatus.AppendText("number of all host " + allHost.Count().ToString());
            // this.txtStatus.AppendText("number of active host " + activeHost.Count().ToString());
            // this.txtStatus.AppendText("number of suspended host " + suspendedHost.Count().ToString());
            //text =;

          
                // It's on a different thread, so use Invoke.
                SetTextCallback d = new SetTextCallback(SetText);
                if (allHost != null)
                {
                    this.Invoke(d, new object[] { "****************************\n" });
                    this.Invoke(d, new object[] { "Number of all host " + allHost.Count().ToString() + "\n" });
                    this.Invoke(d, new object[] { "Number of active host " + activeHost.Count().ToString() + "\n" });
                    this.Invoke(d, new object[] { "Number of suspended host " + suspendedHost.Count().ToString() + "\n" });
                    this.Invoke(d, new object[] { "****************************\n" });

                    
                    do
                    {
                        this.Invoke(d, new object[] { "-------------Start---------------\n" });
                        if (suspendedHost != null)
                        {
                            if (suspendedHost.Count() != 0)
                            {
                                this.Invoke(d, new object[] { "Not all hosts are active, number of host suspended : " + suspendedHost.Count().ToString() + "\n" });
                                runTest = false;
                                break;

                            }
                        }
                        Random random = new Random((int)DateTime.Now.Ticks);
                        int numberOfHostsToSuspendandActivate = random.Next(1, activeHost.Count);
                        this.Invoke(d, new object[] { "Number of Hosts to suspend " + numberOfHostsToSuspendandActivate.ToString() + "\n" });
                        List<OrleansHostStateData> orleansHostDatas = new List<OrleansHostStateData>();
                        ArrayList randomNumberList = new ArrayList();

                        //List <OrleansHostStateData>
                        this.Invoke(d, new object[] { "Suspending .....\n" });
                        for (int i = 1; i <= numberOfHostsToSuspendandActivate; i++)
                        {
                            int randomHost = random.Next(0, activeHost.Count);
                            OrleansHostStateData orleansData = new OrleansHostStateData();
                            if (randomNumberList.Contains(randomHost))
                            {
                                i = i - 1;
                            }
                            else
                            {

                                randomNumberList.Add(randomHost);
                                orleansData.OrleansHostID = activeHost[randomHost].HostId;
                                this.Invoke(d, new object[] { "Host : " + activeHost[randomHost].HostId.ToString() + "\n" });
                                orleansData.OrleansHostState = OrleansStates.Suspend;
                                orleansHostDatas.Add(orleansData);
                            }
                        }

                        marloweApp.UpdateOrleansHost(txtOrleanServer.Text, txtOrleanPort.Text, orleansHostDatas, out errstring);

                        if (!ewait.WaitOne(1200 * 1000, false))
                        {
                            this.Invoke(d, new object[] { "Suspend Host Wait event timed out\n" });
                        }
                        this.Invoke(d, new object[] { "Suspended\n" });
                        this.Invoke(d, new object[] { "Thread will wait for " + this.txtWait.Text + " seconds \n" });
                        Thread.Sleep(int.Parse(this.txtWait.Text) * 1000);

                        //foreach (OrleansHostStateData data in orleansHostDatas )
                        this.Invoke(d, new object[] { "Resuming .....\n" });
                        for (int j = 0; j < orleansHostDatas.Count(); j++)
                        {
                            //OrleansHostStateData orleansData = new OrleansHostStateData();
                            this.Invoke(d, new object[] { "Host : " + orleansHostDatas[j].OrleansHostID.ToString() + "\n" });
                            orleansHostDatas[j].OrleansHostState = OrleansStates.Active;
                        }


                        marloweApp.UpdateOrleansHost(txtOrleanServer.Text, txtOrleanPort.Text, orleansHostDatas, out errstring);

                        if (!ewait.WaitOne(1200 * 1000, false))
                        {
                            this.Invoke(d, new object[] { "Resume Host Wait event timed out\n" });
                        }
                        this.Invoke(d, new object[] { "Resumed.\n" });
                        this.Invoke(d, new object[] { "Thread will wait for " + this.txtWait.Text + " seconds \n" });
                        Thread.Sleep(int.Parse(this.txtWait.Text) * 1000);
                        this.Invoke(d, new object[] { "-------------End-----------------\n\n" });
                    } while (runTest);

                }
                else
                {
                    this.Invoke(d, new object[] { "End Test.\nOrleans Server is down\n" });
                }

        }

        private void txtStatus_TextChanged(object sender, EventArgs e)
        {
            txtStatus.SelectionStart = txtStatus.TextLength;
            txtStatus.ScrollToCaret();

        }

       
    }
}
