using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Net.Mail;
using System.Globalization;

namespace Orleans.TestFramework
{
    public enum SEV { ERROR, STATUS, INFO, RESULTS}

    public class Log
    {
        public static List<string> AttachmentFiles = new List<string>();
        public static TestConfig testConfig;
        public static TestResults TestResults;
        private static readonly string DateTimeFormat = "yyyy-MM-dd " + "HH:mm:ss.fff 'GMT'"; // Example: 2010-09-02 09:50:43.341 GMT - Variant of UniversalSorta­bleDateTimePat­tern

        public static void Init(TestConfig testConfig)
        {
            Log.testConfig = testConfig;
            AttachmentFiles.Clear();
            TestResults = new TestResults();
        }

        // writes into 
        //  Console
        //  TestOutputFile
        //  TestResults aggregator, which control the output to HTML email and global results file.
        public static void WriteLine(SEV sev, string component, string format, params object[] args)
        {
            var message = string.Format(format, args);
            var time = DateTime.UtcNow.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
            string s = string.Format("[{0,-20}\t{1,-8}\t{2,24}]\t{3}", time, sev.ToString(), component, message);
            Console.WriteLine(s);
            WriteToFile(s);
            TestResults.AddToHTMLOutput(sev, s);
        }

        private static void WriteToFile(string s)
        {
            if (null == testConfig.TestOutputFile) return;
            try
            {
                lock (testConfig)
                {
                    using (StreamWriter writer = new StreamWriter(testConfig.TestOutputFile, append: true))
                    {
                        writer.WriteLine(s);
                        writer.Flush();
                    }
                }
            }
            catch
            {
            }
        }

        public static void SendLogs(string subject, string body)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    string title = (testConfig.Variables["BuildNumber"] ?? string.Empty) + ":" + subject;
                    MailMessage message = new MailMessage(testConfig.From, testConfig.To, title, body);
                    message.Attachments.Add(new Attachment(testConfig.TestOutputFile));
                    message.IsBodyHtml = true;
                    //foreach (string s in attachments)
                    //{
                    //    message.Attachments.Add(new Attachment(s));
                    //}
                    foreach (string s in AttachmentFiles)
                    {
                        try
                        {
                            Attachment attachment = new Attachment(s);
                            message.Attachments.Add(attachment);
                        }
                        catch (Exception exc)
                        {
                            Log.WriteLine(SEV.ERROR, "SendLogs Exception", "{0}", exc);
                        }
                    }
                    SmtpClient smtpClient = new SmtpClient();
                    smtpClient.Host = testConfig.Host;
                    smtpClient.Port = testConfig.Port;
                    smtpClient.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials;
                    smtpClient.Send(message);
                    break;
                }
                catch (SmtpException)
                {
                }
            }
        }

        public static string GetResultsHtml(string testName, UnitTestOutcome outcome)
        {
            return string.Format(
@"
<HTML>
<body>
<table border=""1"">
<tr>
<td> Test Name </td> <td> <b> {0} </b> </td>
</tr>
<tr>
<td> Results </td> <td> <b>{1} </b> </td>
</tr>
<tr>
<td> Details </td> <td>{2} </td>
</tr>
<table>
</body>
</HTML>
", testName, outcome, TestResults.GetResultsHtml());
        }
    }

    public class TestResults
    {
        internal string GlobalResultsFileName { get; set; }
        internal string LogDirectory { get; set; }

        private readonly List<Record> AllResultsTable;
        private readonly List<string> Headers;
        private readonly StringBuilder HTMLOutput;
        private int generation = 0;
        private static readonly string NUMBER_HEADER = "No.";

        public TestResults()
        {
            LogDirectory = ".";
            HTMLOutput = new StringBuilder();
            AllResultsTable = new List<Record>();
            Headers = new List<string>();
        }

        public void AddHeaders(IEnumerable<string> headerKeys)
        {
            this.Headers.Add(NUMBER_HEADER);
            this.Headers.AddRange(headerKeys);
            
            Log.WriteLine(SEV.STATUS, "Metrics.Init", "Creating global results log at {0}", GlobalResultsFileName);
            using (StreamWriter writer = new StreamWriter(GlobalResultsFileName, append: true))
            {
                StringBuilder output = new StringBuilder();
                foreach (string key in this.Headers)
                {
                    output.AppendFormat(",{0}", key);
                }
                writer.WriteLine(output);
                writer.Flush();
            }
        }

        // writes a new results record to GlobalResultsFileName file 
        // and also adds to local ResultsTable, for later console outputting.
        public void AddRecord(int linePrinted, Record record)
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(GlobalResultsFileName, append: true))
                {
                    StringBuilder output = new StringBuilder();
                    foreach (string key in this.Headers)
                    {
                        string line = string.Format(",{0}", record.ContainsKey(key) ? record[key] : string.Empty);
                        output.Append(line);
                    }
                    writer.WriteLine(output);
                    writer.Flush();

                    this.AllResultsTable.Add(record);
                }
            }
            catch (Exception exc)
            {
                String str = "Append to file " + GlobalResultsFileName + " failed with exception: " + exc.ToString();
                Log.WriteLine(SEV.ERROR, "TestResults", str, exc);
                //throw new Exception(str, exc);
            }
        }

        // copy GlobalResultsFileName into CheckPoint
        public void CheckPointEarlyResults()
        {
            string checkPointFile = Path.Combine(LogDirectory, "CheckPoint" + generation.ToString() + ".csv");
            File.Copy(GlobalResultsFileName, checkPointFile);
            //Log.AttachmentFiles.Add(checkPointFile);
            Log.WriteLine(SEV.STATUS, "Metric.Init", "Saving Early Results in file check point file: {0}", checkPointFile);
            generation++;
        }

        public void AddToHTMLOutput(SEV sev, string s)
        {
            HTMLOutput.Append(ConvertToHTML(sev, s));
        }

        public static string ConvertToHTML(SEV sev, string s)
        {
            StringBuilder output = new StringBuilder();
            if (sev == SEV.ERROR) output.AppendFormat("<span><b>{0}</b></span><br/>\n", s);
            if (sev == SEV.STATUS) output.AppendFormat("<span>{0}<span><br/>\n", s);
            return output.ToString();
        }

        public string GetResultsHtml()
        {
            string results = SnapshotResults();
            return HTMLOutput.Append(results).ToString();
        }

        // snapshot accumulated results so far into:
        // HTML, console and log.
        private string SnapshotResults()
        {
            Log.WriteLine(SEV.STATUS, "Metric.RESULTS", "----------------");
            StringBuilder output = new StringBuilder();
            int numResults = this.AllResultsTable.Count;
            // Print the average/max/min/std result stats
            foreach (string key in this.Headers.Where(h => !h.Equals(NUMBER_HEADER)))
            {
                RecordAnalyzer analyzer = new RecordAnalyzer(this.AllResultsTable, key);
                var average = analyzer.GetAverage();
                var max = analyzer.GetMax();
                var min = analyzer.GetMin();
                var firstNorm = analyzer.Get1stNorm();
                var std = analyzer.GetStd();
                var sum = analyzer.GetSum(); 
                string line = null;
                if (average is Double)
                {
                    line = string.Format("{0} metric for {1} records: Average = {2:F1}, Max = {3:F1}, Min = {4:F1}, 1st norm = {5:F1}, Std (2nd norm) = {6:F1}, Sum = {7:F1},", 
                                key, numResults, average, max, min, firstNorm, std, sum);
                }else
                {
                    line = string.Format("{0} metric for {1} records: Average = {2}, Max = {3}, Min = {4}, 1st norm = {5}, Std (2nd norm) = {6}, Sum = {7},",
                                key, numResults, average, max, min, firstNorm, std, sum);
                }
                Log.WriteLine(SEV.RESULTS, "Metric.RESULTS", "{0}", line);
                output.AppendFormat(ConvertToHTML(SEV.STATUS, line));
            }

            // Print the result table
            Log.WriteLine(SEV.RESULTS, "Metric.RESULTS", "{0}", string.Join("\t", this.Headers.ToArray()));

            output.AppendFormat("<table border=\"1\"");
            string headerInHTML = RowToHtmlString(this.Headers);
            output.AppendFormat(headerInHTML);

            int linePrinted = 1;
            int numEarlyResults = (MetricCollector.EarlyResultCount >= 0) ? MetricCollector.EarlyResultCount : this.AllResultsTable.Count();
            foreach (Record record in this.AllResultsTable)
            {
                // print first 40 lines plus every 10th line
                bool shouldPrint = linePrinted <= numEarlyResults || ((linePrinted % 10) == 0);
                if (shouldPrint)
                {
                    List<string> row = RecordToRow(linePrinted++, record);

                    Log.WriteLine(SEV.RESULTS, "Metric.RESULTS", "{0}", string.Join("\t", row.ToArray()));
                    string htmlLine = RowToHtmlString(row);
                    output.AppendFormat(htmlLine);
                }
            }
            output.AppendFormat("</table>");
            return output.ToString();
        }

        private List<string> RecordToRow(int linePrinted, Record record)
        {
            List<string> row = new List<string>();
            foreach (string key in this.Headers)
            {
                string line = null;
                if (key.Equals(NUMBER_HEADER))
                {
                    line = string.Format("{0:D3}", linePrinted);
                }
                else
                {
                    line = string.Format("{0}", record.ContainsKey(key) ? record[key] : string.Empty);
                }
                row.Add(line);
            }
            return row;
        }

        private string RowToHtmlString(List<string> row)
        {
            StringBuilder output = new StringBuilder();
            output.AppendFormat("<tr>");
            foreach (string cell in row)
                output.AppendFormat("<td>{0}</td>", cell);
            output.AppendFormat("</tr>");
            return output.ToString();
        }
    }
}
