using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.ServiceModel;
using System.Windows.Forms;
using OrleansUnitTestContainerLibrary;

namespace OrleansUnitTestContainer
{
    public partial class ServiceEndpoints : Form
    {
        public ServiceEndpoints()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(this.textBox1.Text))
            {
                this.listView1.Items.Add(this.textBox1.Text);
            }

        }

        private void button2_Click(object sender, EventArgs e)
        {
            if (this.listView1.SelectedItems.Count > 0)
            {
                foreach (ListViewItem item in listView1.SelectedItems)
                {
                    this.listView1.Items.Remove(item);
                }
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            IUnitTestBusinessLogic businessLogicComponent = null;
            if (this.listView1.SelectedItems.Count > 0)
            {
                string service = "localhost:9000/UnitTestService";
                foreach (ListViewItem item in listView1.SelectedItems)
                {
                    try
                    {
                        service = item.Text;
                        EndpointAddress ep = new EndpointAddress("net.tcp://" + service);
                        InstanceContext context = new InstanceContext(this);
                        businessLogicComponent = DuplexChannelFactory<IUnitTestBusinessLogic>.CreateChannel(context, new NetTcpBinding(), ep);
                        businessLogicComponent.Subscribe();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, string.Format("Unable to connect to service endpoint \"{0}\"", service));
                    }
                }
            }

        }

        private void ServiceEndpoints_Load(object sender, EventArgs e)
        {
            // TODO
            // Load service endponts from configuation

        }
    }
}
