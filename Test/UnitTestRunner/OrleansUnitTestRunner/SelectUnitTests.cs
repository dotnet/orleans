using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Reflection;

namespace OrleansUnitTestContainer
{
    public partial class SelectUnitTests : Form
    {
        public SelectUnitTests()
        {
            InitializeComponent();
        }

        public ListView SelectedUnitTests
        {
            get
            {
                return this.UnitTestList;
            }
        }

        public string OrderBy
        {
            get;
            set;
        }

        public SelectUnitTests(List<MethodInfo> methodInfos)
        {
            InitializeComponent();
            Dictionary<string, ListViewGroup> groups = new Dictionary<string,ListViewGroup>();
            foreach (MethodInfo methodInfo in methodInfos)
            {
                string groupName = methodInfo.DeclaringType.FullName;
                if (!groups.ContainsKey(groupName))
                {
                    groups.Add(groupName, new ListViewGroup(groupName));
                    this.UnitTestList.Groups.Add(groups[groupName]);
                }
                ListViewItem listItem = new ListViewItem(methodInfo.Name, groups[groupName]);
                listItem.Tag = methodInfo;
                listItem.Selected = true;
                this.UnitTestList.Items.Add(listItem);
            }
            SelectAll.Checked = true;
            
        }

        private void radioButton1_CheckedChanged(object sender, EventArgs e)
        {
        }

        private void SelectAll_CheckedChanged(object sender, EventArgs e)
        {
            foreach (ListViewItem item in UnitTestList.Items)
            {
                item.Selected = SelectAll.Checked;        
            }

        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (alpha.Checked)
            {
                OrderBy = "ALPHA";
            }
            else if (random.Checked)
            {
                OrderBy = "RANDOM";
            }
            else if (testClass.Checked)
            {
                OrderBy = "TESTCLASS";
            }
        }
    }
}
