using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace OrleansUnitTestContainer
{
    public partial class Options : Form
    {
        public string DirectoryPath
        {
            get;
            set;
        }
        public Options()
        {
            InitializeComponent();
        }

        private void browseBtn_Click(object sender, EventArgs e)
        {
            folderBrowserDialog1.SelectedPath = DirectoryPath;
            if (folderBrowserDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                directoryTextBox.Text = folderBrowserDialog1.SelectedPath;
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            DirectoryPath = directoryTextBox.Text;
        }

        private void Options_Load(object sender, EventArgs e)
        {
            directoryTextBox.Text = DirectoryPath;
        }
    }
}
