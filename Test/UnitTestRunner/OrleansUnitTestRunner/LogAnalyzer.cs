using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Diagnostics;
using LogicAnalyzerBusinessLogic;

namespace OrleansUnitTestContainer
{
    public partial class LogProcessor : Form
    {
        private Color[] Palette = { Color.LightBlue, Color.LightSeaGreen, Color.LightGoldenrodYellow, Color.LightSalmon, Color.LightSteelBlue, Color.Goldenrod, Color.LightCyan };
        private LogicAnalyzerBusinessLogic.LogFileProcessor logicAnalyzer = new LogicAnalyzerBusinessLogic.LogFileProcessor();
        private Dictionary<string, Color> rowBkColors = new Dictionary<string, Color>();
        public LogProcessor()
        {
            InitializeComponent();
            StartInterval = DateTime.MinValue;
            EndInterval = DateTime.MaxValue;
            FilterFlag = FilterFlag.ERROR | FilterFlag.WARNING | FilterFlag.INFO;
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
        }

        public string[] FileNames
        {
            get;
            set;
        }

        void logicAnalyzer_OnCompletion(string obj, int count, int totalProcessed)
        {
            string logFileName = (string)obj;
            this.Invoke(new Action<string>(UpdateStatusBarText), string.Format("Completed processing log file {0}", logFileName));
        }

        void logicAnalyzer_OnProcessLogEntry(string logFileName, int currentLogEntry)
        {
            try
            {
                this.Invoke(new Action<string>(UpdateStatusBarText), string.Format("Currently processing log file {0} line is {1}", logFileName, currentLogEntry));
            }
            catch (Exception ex)
            {
            }
        }

        private void UpdateStatusBarText(string text)
        {
            this.dataGridView1.Cursor = Cursors.Arrow;
            this.logRecordBindingSource1.Clear();
            this.logRecordBindingSource1.DataSource = logicAnalyzer.LogRecords;
            //this.toolStripStatusLabel1.Text = text;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            if (FileNames.Length > 0)
            {
                RetrieveRecords(new FilterCriteria { endInterval = this.EndInterval, startInterval = this.StartInterval, filterFlag = this.FilterFlag });
            }

        }

        private void openToolStripButton_Click(object sender, EventArgs e)
        {

        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            
        }

        private void bindingNavigatorMoveFirstItem_Click(object sender, EventArgs e)
        {

        }

        public FilterFlag FilterFlag
        {
            get;
            set;
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

        public bool SortByDateTime
        {
            get;
            set;
        }

        private void openToolStripButton_Click_1(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                //logicAnalyzer.OnProcessLogEntry += new Action<string, int>(logicAnalyzer_OnProcessLogEntry);
                RetrieveRecords(new FilterCriteria { endInterval = this.EndInterval, startInterval = this.StartInterval, filterFlag = this.FilterFlag });
            }

        }

        private void dataGridView1_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (IsANonHeaderLinkCell(e))
            {
                if (!((DataGridViewLinkCell)dataGridView1.Rows[e.RowIndex].Cells[e.ColumnIndex]).LinkVisited)
                {
                    //this.Cursor = Cursors.WaitCursor;
                    LogRecord logRecord = ((LogRecord[])logRecordBindingSource1.DataSource)[e.RowIndex];
                    long filePosition = logRecord.filePosition;
                    //LogRecord newRecord = logicAnalyzer.RetrieveLogRecordFrom(logRecord.logFilePath, logRecord.filePosition, logRecord.linePosition);
                    //logRecord.message = newRecord.message;
                    //logRecord.exceptionInfo = newRecord.exceptionInfo;
                    //this.Cursor = Cursors.Arrow;
                    LogViewer logViewer = new LogViewer();
                    logViewer.MdiParent = this.MdiParent;
                    logViewer.Show();
                }
                MessageBox.Show(((DataGridViewLinkCell)dataGridView1.Rows[e.RowIndex].Cells[e.ColumnIndex]).FormattedValue.ToString());
            }
            else if (IsANonHeaderButtonCell(e))
            {
            }
        }

        private bool IsANonHeaderLinkCell(DataGridViewCellEventArgs cellEvent)
        {
            if (dataGridView1.Columns[cellEvent.ColumnIndex] is
                DataGridViewLinkColumn &&
                cellEvent.RowIndex != -1)
            { return true; }
            else { return false; }
        }

        private bool IsANonHeaderButtonCell(DataGridViewCellEventArgs cellEvent)
        {
            if (dataGridView1.Columns[cellEvent.ColumnIndex] is
                DataGridViewButtonColumn &&
                cellEvent.RowIndex != -1)
            { return true; }
            else { return (false); }
        }

        private void FilterResultsButton(object sender, EventArgs e)
        {
            FilterDialog filterDlg = new FilterDialog();
            filterDlg.StartInterval = StartInterval;
            filterDlg.EndInterval = EndInterval;
            filterDlg.Errors = (FilterFlag & FilterFlag.ERROR) != 0;
            filterDlg.Warnings = (FilterFlag & FilterFlag.WARNING) != 0;
            filterDlg.Infos = (FilterFlag & FilterFlag.INFO) != 0;

            if (filterDlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                FilterFlag = 0;
                if (filterDlg.Infos)
                {
                    FilterFlag |= FilterFlag.INFO;
                }
                if (filterDlg.Warnings)
                {
                    FilterFlag |= FilterFlag.WARNING;
                }
                if (filterDlg.Errors)
                {
                    FilterFlag |= FilterFlag.ERROR;
                }

                StartInterval = filterDlg.StartInterval;
                EndInterval = filterDlg.EndInterval;

                if (FileNames.Length > 0)
                {
                    RetrieveRecords(new FilterCriteria { endInterval = this.EndInterval, startInterval = this.StartInterval, filterFlag = this.FilterFlag });
                }
            }
        }

        private void RetrieveRecords(FilterCriteria filterCriteria)
        {
            System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
            ProgressDlg progressDlg = new ProgressDlg(cts);
            logicAnalyzer.OnCompletion += (fileName, b, c) =>
            {
                string logFileName = (string)fileName;
                this.Invoke(new Action<string>(UpdateStatusBarText), string.Format("Completed processing log file {0}", logFileName));
                this.Invoke(new Action(progressDlg.OnCompletion));
            };

            logicAnalyzer.OnProcessLogEntry += (fileName, lineNumber) =>
            {
                this.Invoke(new Action<string, int>(progressDlg.OnProgress), fileName, lineNumber);
            };

            Task task = new Task((obj) =>
            {
                logicAnalyzer.LogFilter = filterCriteria;
                ((LogFileProcessor)obj).ProcessLogFiles(FileNames, -1, cts.Token);
            }, logicAnalyzer, cts.Token);

            task.Start();
            progressDlg.ShowDialog(this);
        }

        private void toolStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void dataGridView1_RowPrePaint(object sender, DataGridViewRowPrePaintEventArgs e)
        {
            LogRecord[] records = (LogRecord[])logRecordBindingSource1.DataSource;
            if (records != null && records.Length > e.RowIndex)
            {
                string fileName = records[e.RowIndex].logFilePath;
                if (!rowBkColors.ContainsKey(fileName))
                {
                    rowBkColors.Add(fileName, Palette[rowBkColors.Keys.Count() % Palette.Length]);
                }

                System.Drawing.Color bkColor = rowBkColors[fileName];
                using (Brush backBrush = new System.Drawing.SolidBrush(bkColor))
                {                        
                    e.Graphics.FillRectangle(backBrush, e.RowBounds);
                    e.PaintCells(e.RowBounds, (DataGridViewPaintParts.All ^ DataGridViewPaintParts.Background));
                    e.Handled = true;
                }

            }
        }

        private void dataGridView1_CellContextMenuStripNeeded(object sender, DataGridViewCellContextMenuStripNeededEventArgs e)
        {
            ContextMenuStrip menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open log file at this location...", null, new EventHandler(OnOpenLogFilesContextMenuClick));
            e.ContextMenuStrip = menu;
        }

        private void OnOpenLogFilesContextMenuClick(object source, EventArgs args)
        {
        }
    }
}
