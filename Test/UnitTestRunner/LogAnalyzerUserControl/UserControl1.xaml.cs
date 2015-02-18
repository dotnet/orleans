using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace LogAnalyzerUserControl
{
    /// <summary>
    /// Interaction logic for UserControl1.xaml
    /// </summary>
    public partial class LogFileViewerControl : UserControl
    {
        public LogFileViewerControl()
        {
            InitializeComponent();
        }

        public void OpenLogFiles(string[] fileNames)
        {
            if (fileNames.Length > 0)
                textBox1.Text = fileNames[0];
            if (fileNames.Length > 1)
                textBox2.Text = fileNames[1];
            if (fileNames.Length > 2)
                textBox3.Text = fileNames[2];
            if (fileNames.Length > 3)
                textBox4.Text = fileNames[3];
        }
    }
}
