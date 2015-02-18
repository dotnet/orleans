using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Reflection;
using System.Threading;
using OrleansUnitTestContainerLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OrleansUnitTestContainer
{
    public partial class TestRunner : Form
    {
        enum RunStatus  {Running, Cancelling, Stopped};
        private string logPath = System.Environment.CurrentDirectory;
        private string lastLogPath = null;
        private IUnitTestBusinessLogic businessLogicComponent = null;
        private int testsRun;
        private int testsPassed;
        private System.Threading.CancellationTokenSource cts = new CancellationTokenSource();
        private string orderBy = "TESTCLASS";
        private bool isChecked = true;
        private RunStatus currentStatus = RunStatus.Stopped;
        public string FileName
        {
            get;
            set;
        }

        private bool IsChecked
        {
            get
            {
                return isChecked;
            }
            set
            {
                isChecked = value;
            }
        }

        public string LogPath
        {
            get
            {
                return logPath;
            }
            set
            {
                logPath = value;
            }
        }

        public string OrleansStartupDirectory
        {
            get;
            set;
        }

        public TestRunner(string fileName)
        {
            FileName = fileName;
            InitializeComponent();
            businessLogicComponent = new UnitTestBusinessLogic();
            businessLogicComponent.OnTestSuccess += new Action<string, string>(businessLogicComponent_OnTestSuccess);
            businessLogicComponent.OnTestFailure += new Action<string, string, string>(businessLogicComponent_OnTestFailure);
            businessLogicComponent.OnStartTestRun += new Action<MethodInfo[]>(businessLogicComponent_OnStartTestRun);
            businessLogicComponent.OnTestRunCompletion += new Action<int, int, long>(businessLogicComponent_OnTestRunCompletion);
            businessLogicComponent.OnLoadTestUnitDll += new Action<string>(businessLogicComponent_OnLoadTestUnitDll);
            businessLogicComponent.OnStartTest += new Action<string, string>(businessLogicComponent_OnStartTest);
            businessLogicComponent.OnCancel += new Action(businessLogicComponent_OnCancel);
            businessLogicComponent.OnUnhandledException += new Action<object>(businessLogicComponent_OnUnhandledException);
        }

        void businessLogicComponent_OnUnhandledException(object obj)
        {
            System.Diagnostics.Trace.WriteLine(string.Format("Unhandled exception encountered, application is terminating {0}", obj.ToString()));
            MessageBox.Show(obj.ToString(), "Unhandled excpetion encountered, application is terminating");
        }


        void businessLogicComponent_OnCancel()
        {
            this.Invoke(new Action(() => OnCancel()));
        }


        void businessLogicComponent_OnStartTestRun(MethodInfo[] obj)
        {
            this.Invoke(new Action(() => OnStartTestRun(obj)));
        }

        void businessLogicComponent_OnTestSuccess(string arg1, string arg2)
        {
            this.Invoke(new Action(() => OnTestSuccess(arg1, arg2)));
        }

        void businessLogicComponent_OnTestRunCompletion(int arg1, int arg2, long arg3)
        {
            this.Invoke(new Action(() =>
            {
                OnTestRunCompletion(arg1, arg2, arg3);
            }));
        }

        void businessLogicComponent_OnTestFailure(string arg1, string arg2, string arg3)
        {
            this.Invoke(new Action(() =>
            {
                OnTestFailure(arg1, arg2, arg3);
            }));
        }

        void businessLogicComponent_OnStartTest(string methodName, string declaringType)
        {
            this.Invoke(new Action(() =>
            {
                OnStartTest(methodName, declaringType);
            }));
        }

        void businessLogicComponent_OnLoadTestUnitDll(string obj)
        {
        }

        public void OnCancel()
        {
            currentStatus = RunStatus.Stopped;
            foreach (ListViewItem item in this.listView1.Items)
            {
                if (item.SubItems[0].Text == "Pending")
                {
                    item.ImageIndex = 4;
                    item.SubItems[0].Text = "Canceled";
                }
            }
        }

        public void OnStartTest(string methodName, string declaringType)
        {
            ListViewItem item = FindListItem(methodName, declaringType);
            if (item != null)
            {
                item.SubItems[0].Text = "In Progress";
                item.ImageIndex = 3;
            }
        }
        public void OnStartTestRun(MethodInfo[] methodInfos)
        {
            try
            {
                LogPath = ((MDIMainWindow)this.MdiParent).LogPath;
                if (!string.IsNullOrEmpty(LogPath))
                {
                    string logPath = System.IO.Path.Combine(new string[] { LogPath, "Logs", DateTime.Now.ToLongDateString() });
                    int runs = 2;
                    while (System.IO.Directory.Exists(logPath))
                    {
                        logPath = System.IO.Path.Combine(new string[] { LogPath, "Logs", DateTime.Now.ToLongDateString() });
                        logPath += string.Format("(Run {0})", runs++);
                    }

                    System.IO.Directory.CreateDirectory(logPath);
                    lastLogPath = logPath;
                    businessLogicComponent.LogPath = logPath;
                    string startupDirectory = ((MDIMainWindow)this.MdiParent).OrleanStartUpDirectory;
                    if (!string.IsNullOrEmpty(startupDirectory) && System.IO.Directory.Exists(startupDirectory))
                    {
                        businessLogicComponent.StartUpDirectory = ((MDIMainWindow)this.MdiParent).OrleanStartUpDirectory;
                        Initialize(methodInfos);
                    }
                    else
                    {
                        MessageBox.Show("Input directory does not exist!");
                        cts.Cancel();
                    }
                }
                else
                {
                    MessageBox.Show("Log file path cannot be empty");
                    cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Unable to create log path directory " + logPath);
                cts.Cancel();
            }
        }

        public void OnTestRunCompletion(int passed, int failed, long duration)
        {
            currentStatus = RunStatus.Stopped;
            this.statusStrip1.Items["toolStripStatusLabel1"].Text = string.Format("Test Run Completed Results {0}/{1} passed, total test duration {2} ms", passed, failed, duration);
            
        }

        public void OnTestSuccess(string methodName, string declaringType)
        {
            ListViewItem listItem = FindListItem(methodName, declaringType);
            if (listItem == null)
            {
                listItem = new ListViewItem(new string[] {"", methodName, declaringType, ""});
                listView1.Items.Add(listItem);
            }
            UpdateListItemSuccess(listItem);
            ((ToolStripProgressBar)this.statusStrip1.Items["toolStripProgressBar2"]).Value = testsRun;
        }

        public void OnTestFailure(string methodName, string declaringType, string errorMsg)
        {
            ListViewItem listItem = FindListItem(methodName, declaringType);
            if (listItem == null)
            {
                listItem = new ListViewItem(new string[] { "", methodName, declaringType, "" });
                listView1.Items.Add(listItem);
            }
            UpdateListItemFailure(listItem, errorMsg);
            ((ToolStripProgressBar)this.statusStrip1.Items["toolStripProgressBar2"]).Value = testsRun;
        }

        private ListViewItem FindListItem(string methodName, string declaringType)
        {
            foreach (ListViewItem item in listView1.Items)
            {
                declaringType = declaringType.Substring(declaringType.IndexOf('.') + 1);
                if (item.SubItems[1].Text == methodName)
                {
                    return item;
                }
            }

            return null;
        }

        public string DefaultDirectory
        {
            get;
            set;
        }

        private void unitTestDLLToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void testUnitDLLToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.MdiParent.Close();
        }

        private void TestResults_SizeChanged(object sender, EventArgs e)
        {

        }


        private void OnRunTests_Click(object sender, EventArgs e)
        {       
            // run tests
            if (currentStatus == RunStatus.Stopped )
            {
                currentStatus = RunStatus.Running;
                cts = new CancellationTokenSource();
                List<MethodInfo> unitTests = new List<MethodInfo>();
                MethodInfo[] methods = businessLogicComponent.LoadUnitTestDll(FileName);
                foreach (MethodInfo info in methods)
                {
                    ListViewItem item = FindListItem(info.Name, info.DeclaringType.FullName);
                    if (item != null && item.Checked)
                    {
                        unitTests.Add(info);
                    }
                }
                System.Threading.Tasks.TaskFactory factory = new System.Threading.Tasks.TaskFactory(cts.Token);
                factory.StartNew(new Action<object>((f) =>
                {
                    unitTests = new List<MethodInfo>(OrderUnitTests(orderBy, unitTests));
                    businessLogicComponent.RunUnitTests(unitTests.ToArray(), (f as System.Threading.Tasks.TaskFactory).CancellationToken);
                }), factory, cts.Token);
            }
            else
            {
                MessageBox.Show("Unit test run in progress, you must cancel first before re-starting");
            }

        }


        private MethodInfo[] OrderUnitTests(string testMode, List<MethodInfo> unitTests)
        {
            if (testMode != null)
            {
                switch (testMode.ToUpper())
                {
                    case "TESTCLASS":
                        return unitTests.OrderBy(new Func<MethodInfo, string>((methodInfo) =>
                        {
                            return methodInfo.DeclaringType.FullName;
                        })).ToArray();
                    case "ALPHA":
                        return unitTests.OrderBy(new Func<MethodInfo, string>((methodInfo) =>
                        {
                            return methodInfo.Name;
                        })).ToArray();
                    case "RANDOM":
                        Random rand = new Random();
                        List<MethodInfo> newUnitTests = new List<MethodInfo>();

                        HashSet<int> indexes = new HashSet<int>();
                        while (newUnitTests.Count() < unitTests.Count())
                        {
                            int nextIndex = rand.Next(unitTests.Count());
                            if (!indexes.Contains(nextIndex))
                            {
                                indexes.Add(nextIndex);
                                newUnitTests.Add(unitTests[nextIndex]);
                            }
                        }

                        return newUnitTests.ToArray();
                    default:
                        return unitTests.ToArray();
                }
            }
            return unitTests.ToArray();
        }
        private void UpdateListItemFailure(ListViewItem listItem, string errorMsg)
        {
            testsRun++;
            if (listItem != null)
            {
                listItem.SubItems[0].Text = "Failed";
                listItem.SubItems[3].Text = errorMsg;
                listItem.ImageIndex = 2;
            }
        }

        private void UpdateListItemSuccess(ListViewItem listItem)
        {
            testsRun++;
            testsPassed++;
            if (listItem != null)
            {
                listItem.ImageIndex = 0;
                listItem.SubItems[0].Text = "Passed";
                this.statusStrip1.Items["toolStripStatusLabel1"].Text = string.Format("Results {0}/{1} passed", testsPassed, listView1.Items.Count);
                listItem.EnsureVisible();
            }
        }

        private void Initialize(MethodInfo[] methodInfos)
        {

            testsRun = 0;
            //listView1.Items.Clear();
            this.statusStrip1.Items["toolStripStatusLabel1"].Text = string.Format("Results {0}/{1} passed", 0, methodInfos.Length);
            // reset status to uninitialized

            foreach (ListViewItem item in listView1.Items)
            {
                item.SubItems[0].Text = "";
                item.SubItems[3].Text = "";
                item.ImageIndex = -1;
            }
            
            foreach (MethodInfo info in methodInfos)
            {
                ListViewItem listItem = FindListItem(info.Name, info.DeclaringType.FullName);
                if (listItem != null)
                {
                    listItem.SubItems[0].Text = "Pending";
                    listItem.SubItems[1].Text = info.Name;
                    listItem.SubItems[2].Text = info.DeclaringType.FullName;
                    listItem.SubItems[3].Text = "";
                    listItem.ImageIndex = 1;
                }
                //listItem.Tag = methodInfo;
            }
            this.statusStrip1.Items["toolStripProgressBar2"].Visible = true;
            ((ToolStripProgressBar)this.statusStrip1.Items["toolStripProgressBar2"]).Maximum = listView1.Items.Count;
            ((ToolStripProgressBar)this.statusStrip1.Items["toolStripProgressBar2"]).Minimum = 0;
            ((ToolStripProgressBar)this.statusStrip1.Items["toolStripProgressBar2"]).Step = 1;
            ((ToolStripProgressBar)this.statusStrip1.Items["toolStripProgressBar2"]).Value = 0;




        }

        private void TestResults_Load(object sender, EventArgs e)
        {
            toolStripComboBox1.SelectedIndex = 2;
            this.Text = FileName;
            MethodInfo[] unitTests = businessLogicComponent.LoadUnitTestDll(FileName);
            if (unitTests.Length == 0)
            {
                MessageBox.Show("This DLL does not appears to contain any unit tests");
            }
            else
            {
                foreach (MethodInfo methodInfo in unitTests)
                {
                    string declaringType = methodInfo.DeclaringType.FullName;
                    declaringType = declaringType.Substring(declaringType.IndexOf('.') + 1);
                    ListViewItem listItem = new ListViewItem(new string[] {"", methodInfo.Name, declaringType, ""});
                    listItem.ImageIndex = -1;
                    listItem.Tag = methodInfo;
                    this.listView1.Items.Add(listItem);
                }
            }
            listView1.Sorting = SortOrder.Descending;
            listView1.ListViewItemSorter = new ListViewItemComparer(1, true);
        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            if (currentStatus == RunStatus.Running)
            {
                currentStatus = RunStatus.Cancelling;
                cts.Cancel();
            }
            else if (currentStatus == RunStatus.Cancelling)
            {
                MessageBox.Show("Cancelling in progress");
            }
        }

        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            IsChecked = !IsChecked;
            if (listView1.SelectedItems.Count > 0)
            {
                foreach (ListViewItem item in listView1.SelectedItems)
                {
                    item.Checked = IsChecked;
                }
            }
            else
            {
                foreach (ListViewItem item in listView1.Items)
                {
                    item.Checked = IsChecked;
                }
            }
        }

        // Implements the manual sorting of items by columns.
        class ListViewItemComparer : System.Collections.IComparer
        {
            private int col;
            bool Ascending = true;
            public ListViewItemComparer()
            {
                col = 0;
            }
            public ListViewItemComparer(int column, bool bAscending)
            {
                col = column;
                Ascending = bAscending;
            }
            public int Compare(object x, object y)
            {
                int i = String.Compare(((ListViewItem)x).SubItems[col].Text, ((ListViewItem)y).SubItems[col].Text);
                if (Ascending)
                    return i;
                else
                {
                    return -i;
                }
            }
        }

        private void listView1_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            switch (listView1.Sorting)
            {
                case SortOrder.Descending:
                    listView1.Sorting = SortOrder.Ascending;
                    listView1.ListViewItemSorter = new ListViewItemComparer(e.Column, true);
                    break;
                case SortOrder.Ascending:
                    listView1.Sorting = SortOrder.Descending;
                    listView1.ListViewItemSorter = new ListViewItemComparer(e.Column, false);
                    break;
                default:
                    listView1.Sorting = SortOrder.Ascending;
                    listView1.ListViewItemSorter = new ListViewItemComparer(e.Column, true);
                    break;
            }
        }

        private void toolStripComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            OrderBy  orderBy = OrderBy.Random;
            switch (toolStripComboBox1.SelectedIndex)
            {
                case 0:
                    orderBy = OrderBy.Random;
                    break;
                case 1:
                    orderBy = OrderBy.TestName;
                    break;
                case 2:
                    orderBy = OrderBy.TestClass;
                    break;
            }
            businessLogicComponent.RunOrder = orderBy;
        }

        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            if (LogPath != null)
            {
                LogProcessor logProcessor = new LogProcessor();
                string[] files = System.IO.Directory.GetFiles(LogPath, "*.log", System.IO.SearchOption.AllDirectories);
                logProcessor.FileNames = files;
                logProcessor.MdiParent = this.MdiParent;
                logProcessor.Show();
            }
            else
            {
                MessageBox.Show("You must first run at least one unit test before examining the log files");
            }
        }

        private void listView1_DoubleClick(object sender, EventArgs e)
        {
            if (lastLogPath != null)
            {
                if (listView1.SelectedItems.Count > 0)
                {
                    ListViewItem item = listView1.SelectedItems[0];
                    string declaringType = item.SubItems[2].Text;
                    string methodName = item.SubItems[1].Text;

                    string logPath = System.IO.Path.Combine(lastLogPath, declaringType, methodName);
                    if (System.IO.Directory.Exists(logPath))
                    {
                        LogProcessor logProcessor = new LogProcessor();
                        string[] files = System.IO.Directory.GetFiles(logPath, "*.log", System.IO.SearchOption.AllDirectories);
                        logProcessor.FileNames = files;
                        logProcessor.MdiParent = this.MdiParent;
                        logProcessor.Show();
                        this.MdiParent.LayoutMdi(MdiLayout.TileHorizontal);

                    }
                }
            }
        }
    }
}
