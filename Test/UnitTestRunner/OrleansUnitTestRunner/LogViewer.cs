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
    public partial class LogViewer : Form
    {
        public LogViewer()
        {
            InitializeComponent();
        }

        private void elementHost1_ChildChanged(object sender, System.Windows.Forms.Integration.ChildChangedEventArgs e)
        {

        }

        private void LogViewercs_Load(object sender, EventArgs e)
        {
            bool b = this.elementHost1.Child.AreAnyTouchesCaptured;
        }
    }
}
