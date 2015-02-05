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
    public partial class FilterDialog : Form
    {
        public bool Errors
        {
            get;
            set;
        }

        public bool Warnings
        {
            get;
            set;
        }

        public bool Infos
        {
            get;
            set;
        }

        public FilterDialog()
        {
            InitializeComponent();
            StartInterval = DateTime.MinValue;
            EndInterval = DateTime.MaxValue;
        }

        public DateTime StartInterval
        {
            get;
            set;
        }

        public DateTime EndInterval
        {
            get;
            set;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Errors = checkedListBox1.CheckedItems.Contains(checkedListBox1.Items[0]);
            Warnings = checkedListBox1.CheckedItems.Contains(checkedListBox1.Items[1]);
            Infos = checkedListBox1.CheckedItems.Contains(checkedListBox1.Items[2]);
            if (this.startDate.Checked)
            {
                StartInterval = this.startDate.Value.Date;
            }
            else
            {
                StartInterval = DateTime.MinValue;
            }
            if (this.endDate.Checked)
            {
                EndInterval = this.endDate.Value.Date;
            }
            else
            {
                EndInterval = DateTime.MaxValue;
            }

        }

        private void FilterDialog_Load(object sender, EventArgs e)
        {
            checkedListBox1.SetItemChecked(0, Errors);
            checkedListBox1.SetItemChecked(1, Warnings);
            checkedListBox1.SetItemChecked(2, Infos);

            if (StartInterval != DateTime.MinValue)
                startDate.Value = StartInterval;
            if (EndInterval != DateTime.MaxValue)
                endDate.Value = EndInterval;            
        }
    }
}
