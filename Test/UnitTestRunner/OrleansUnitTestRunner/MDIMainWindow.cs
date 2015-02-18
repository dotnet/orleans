using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Reflection;
using System.ServiceModel;

namespace OrleansUnitTestContainer
{
    public partial class MDIMainWindow : Form
    {

        public string OrleanStartUpDirectory
        {
            get;
            set;
        }

        public string LogPath
        {
            get;
            set;
        }

        public MDIMainWindow()
        {
            InitializeComponent();
           
        }

        private void MDIMainWindow_Load(object sender, EventArgs e)
        {
            LogPath = Properties.Settings.Default.LogPath;          
            OrleanStartUpDirectory = Properties.Settings.Default.InputPath;
            inputPathTextBox.Text = OrleanStartUpDirectory;
            logPathTextBox.Text = LogPath;

            if (!string.IsNullOrEmpty(Properties.Settings.Default.LastRunDll) && System.IO.File.Exists(Properties.Settings.Default.LastRunDll))
            {
                TestRunner testResults = new TestRunner(Properties.Settings.Default.LastRunDll);
                testResults.MdiParent = this;
                testResults.WindowState = FormWindowState.Maximized;
                testResults.Show();

            }
        }

        private void unitTestDLLToolStripMenuItem_Click(object sender, EventArgs e)
        {
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void tileHorizontalToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.LayoutMdi(MdiLayout.TileHorizontal);
        }

        private void tileVerticalToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.LayoutMdi(MdiLayout.TileVertical);
        }

        private void cascadeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.LayoutMdi(MdiLayout.Cascade);
        }

        private void defaultDirectoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
        }

        private void wCFServiceEndPointToolStripMenuItem_Click(object sender, EventArgs e)
        {
        }

        private void connectToUnitTestServerToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void openUnitTestAssembly_Click(object sender, EventArgs e)
        {
            if (openUnitTestDll.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                foreach (string fileName in openUnitTestDll.FileNames)
                {
                    TestRunner testResults = new TestRunner(fileName);
                    testResults.MdiParent = this;
                    testResults.WindowState = FormWindowState.Maximized;
                    testResults.Show();
                    Properties.Settings.Default.LastRunDll = fileName;
                }
            }
        }

        private void processLogFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenLogDirectory_Click(sender, e);
        }

        private void OpenLogDirectory_Click(object sender, EventArgs e)
        {
            if (openLogsFolder.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                LogProcessor logProcessor = new LogProcessor();
                string[] files = System.IO.Directory.GetFiles(openLogsFolder.SelectedPath, "*.log", System.IO.SearchOption.AllDirectories);
                logProcessor.FileNames = files;
                logProcessor.MdiParent = this;
                logProcessor.Show();
            }
        }

        private void openUnitTestFilessToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openUnitTestAssembly_Click(sender, e);
        }

        private void logPathTextBox_TextChanged(object sender, EventArgs e)
        {
        }

        private void MDIMainWindow_MdiChildActivate(object sender, EventArgs e)
        {
            
        }

        private void setLogPathBtn_Click(object sender, EventArgs e)
        {
            openLogsFolder.SelectedPath = logPathTextBox.Text;
            if (openLogsFolder.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                LogPath = openLogsFolder.SelectedPath;
                logPathTextBox.Text = LogPath;
                Properties.Settings.Default.LogPath = LogPath;
                Properties.Settings.Default.Save();
            }
        }

        private void logPathTextBox_Leave(object sender, EventArgs e)
        {
            LogPath = logPathTextBox.Text;
            Properties.Settings.Default.LogPath = LogPath;
            Properties.Settings.Default.Save();
        }

        private void optionsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Options optionsFrm = new Options();
            optionsFrm.DirectoryPath = OrleanStartUpDirectory;
            if (optionsFrm.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                OrleanStartUpDirectory = optionsFrm.DirectoryPath;
                Properties.Settings.Default.InputPath = OrleanStartUpDirectory;
                Properties.Settings.Default.Save();
            }
        }

        private void MDIMainWindow_FormClosed(object sender, FormClosedEventArgs e)
        {
            Properties.Settings.Default.Save();
        }

        private void setInputPathBtn_Click(object sender, EventArgs e)
        {
            if (openLogsFolder.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                OrleanStartUpDirectory = openLogsFolder.SelectedPath;
                inputPathTextBox.Text = OrleanStartUpDirectory;
                Properties.Settings.Default.InputPath = OrleanStartUpDirectory;
                Properties.Settings.Default.Save();
            }

        }

        private void inputPathTextBox_Leave(object sender, EventArgs e)
        {
            OrleanStartUpDirectory = inputPathTextBox.Text;
            Properties.Settings.Default.InputPath = OrleanStartUpDirectory;
            Properties.Settings.Default.Save();

        }

    }
}
