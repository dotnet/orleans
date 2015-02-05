using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace OrleansUnitTestContainer
{
    public partial class ProgressDlg : Form
    {
        private CancellationTokenSource token;
        private Dictionary<string, int> listItems = new Dictionary<string, int>();
        public ProgressDlg(System.Threading.CancellationTokenSource token)
        {
            InitializeComponent();
            this.token = token;
        }
        private void ProgressDlg_Load(object sender, EventArgs e)
        {

        }

        public void OnCompletion()
        {
            this.Close();
        }

        public void OnProgress(string fileName, int lineNumber)
        {
            if (!listView1.Items.ContainsKey(fileName))
            {               
                ListViewItem listItem =  listView1.Items.Add(fileName, fileName, 0);
                listItem.SubItems.Add("0");
            }
            else
            {
                this.listView1.Items[fileName].SubItems[1].Text = lineNumber.ToString();
            }
        }

        private void CancelBtn_Click(object sender, EventArgs e)
        {
            if (!token.IsCancellationRequested)
            {
                token.Cancel();
            }
        }
    }
}
