using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Office.Interop.Excel;
using System.Xml;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OrleansPerformanceData
{
    public class DataHandler
    {
        Workbook workbook;
        public ReportHandler reportHandler;

        public SortedDictionary<string, Dictionary<string, float>> tests;
        public List<string> builds;

        public List<string> categoryNames; // in desired order
        public Dictionary<string, List<string>> categoryMembers;

        public float colorThresholdLow = 0.75f, colorThresholdHigh = 1.33f;

        public string baseline;

        public DataHandler(Workbook workbook)
        {
            this.workbook = workbook;

            tests = new SortedDictionary<string, Dictionary<string, float>>();
            builds = new List<string>();
            categoryNames = new List<string>();
            categoryMembers = new Dictionary<string, List<string>>();
        }

        public void AddTestData(string testName, string runId, float value)
        {
            if (!tests.ContainsKey(testName))
                tests[testName] = new Dictionary<string, float>();
            tests[testName][runId] = value;
        }

        public void RegisterCategory(string categoryName, string testName)
        {
            if (!categoryNames.Contains(categoryName))
            {
                categoryNames.Add(categoryName);
                categoryMembers[categoryName] = new List<string>();
            }
            if (!categoryMembers[categoryName].Contains(testName))
                categoryMembers[categoryName].Add(testName);
        }

        public Dictionary<string, float> GenerateCategoryAverageComparisons(string baseline, string report)
        {
            Dictionary<string, float> averageComparisons = new Dictionary<string,float>();
            foreach (string categoryName in categoryNames)
            {
                int count = 0;
                float accumulator = 0.0f;
                foreach (string test in categoryMembers[categoryName])
                {
                    if (tests[test].ContainsKey(baseline) && tests[test].ContainsKey(report))
                    {
                        count++;
                        accumulator += (tests[test][report] / tests[test][baseline]);
                    }

                }
                if (count > 0)
                    averageComparisons[categoryName] = accumulator / count;
            }
            return averageComparisons;
        }

        public void SetColorThresholds(float low, float high)
        {
            colorThresholdLow = low;
            colorThresholdHigh = high;
        }

        public void BulkLoad(string path)
        {
            string name = "benchmark*.xml";
            string[] files = System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(path), name, /*Settings1.Default.LogFileName*/ System.IO.SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                foreach (string fileName in files)
                    ImportFile(fileName);
            }
        }

        public void ImportFile(string fileName)
        {
            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load(fileName);
            XmlNodeList nodeList = xmlDoc.SelectNodes("//LogEntry");

            // Get the name of the build:  Either it's in the file, or we pull it from the directory the file was in
            string buildName;
            XmlNode performanceLogNode = xmlDoc.SelectSingleNode("PerformanceLog");
            if (performanceLogNode.Attributes["BuildId"] != null)
            {
                buildName = performanceLogNode.Attributes["BuildId"].Value;
            }
            else
            {
                int lastSlash = fileName.LastIndexOf("\\");
                int penultimateSlash = fileName.LastIndexOf("\\", lastSlash - 1);
                buildName = fileName.Substring(penultimateSlash + 1, (lastSlash - penultimateSlash) - 1);
            }

            // Make sure there's no duplicates
            if (SheetExists(buildName))
            {
                Console.Out.WriteLine("Duplicate build named {0} from file {1}", buildName, fileName);
                return;
            }

            builds.Add(buildName);
            workbook.Sheets.Add(Type: System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), @".\runtemplate.xltx"));
            Microsoft.Office.Interop.Excel.Worksheet sheet = (Microsoft.Office.Interop.Excel.Worksheet)workbook.ActiveSheet;
            sheet.Name = buildName;

            Console.Out.WriteLine("Importing build {0}:  {1}", buildName, fileName);

            /*
            XmlDocument sheetDoc = new XmlDocument();
            XmlElement root = sheetDoc.CreateElement("PerformanceLog");
            sheetDoc.AppendChild(root);
             * */

            // Load the tests
            foreach (XmlNode node in nodeList)
            {
                /*
                XmlNode newNode = sheetDoc.ImportNode(node, true);
                root.AppendChild(newNode);
                 * */

                string testName = node.Attributes["MethodName"].Value.ToString();
                float testValue = float.Parse(node.Attributes["MetricValue"].Value);
                AddTestData(testName, buildName, testValue);

                foreach (XmlNode child in node.ChildNodes)
                    if (child.Name == "Category")
                        RegisterCategory(child.InnerText, testName);
            }

            UpdateSheet(buildName);
        }

        public void UpdateSheet(string sheetName, string baselineName = null)
        {
            Microsoft.Office.Interop.Excel.Worksheet sheet = workbook.Sheets[sheetName];
            this.baseline = baselineName;

            int index = 2;
            foreach (var pair in tests)
            {
                string testName = pair.Key;
                Dictionary<string, float> testData = pair.Value;

                if (testData.ContainsKey(sheetName) || (baselineName != null && testData.ContainsKey(baselineName)))
                    SheetHelper.ExcelCall(() =>
                    {
                        sheet.Cells[index, "A"] = testName;

                        if (baselineName == null || !testData.ContainsKey(baselineName))
                            sheet.Cells[index, "B"] = null;
                        else
                            sheet.Cells[index, "B"] = testData[baselineName];

                        if (!testData.ContainsKey(sheetName))
                            sheet.Cells[index, "C"] = null;
                        else
                            sheet.Cells[index, "C"] = testData[sheetName];

                        if (baselineName != null && testData.ContainsKey(baselineName) && testData.ContainsKey(sheetName))
                            sheet.Cells[index, "D"] = string.Format("=B{0}/C{0}", index);

                        index++;
                    });
            }

            Range formatRange = sheet.Range["D2", "D" + (index - 1)];

            SheetHelper.ApplyColorFormatting(formatRange, colorThresholdLow, colorThresholdHigh);
        }

        public void UpdateSummary(string baselineName)
        {
            Microsoft.Office.Interop.Excel.Worksheet summary = workbook.Sheets["Summary"];

            List<string> runs = new List<string>();

            foreach (Microsoft.Office.Interop.Excel.Worksheet sheet in workbook.Sheets)
            {
                if (sheet.Name != "Summary")
                    runs.Add(sheet.Name);
            }

            for (int i = 0; i < runs.Count; i++)
            {
                summary.Cells[1, 3 + i] = runs[i];
            }

            Range allComparisonHeaders = summary.Range[
                summary.Cells[1, 2],
                summary.Cells[1, 2 + runs.Count]];

            Range allComparisonBaseline = summary.Range[
                summary.Cells[2, 2],
                summary.Cells[2 + tests.Count - 1, 2]];

            Range allComparisonRuns = summary.Range[
                summary.Cells[2, 3],
                summary.Cells[2 + tests.Count - 1, 3 + runs.Count - 1]];

            Range allDataHeaders = summary.Range[
                summary.Cells[2 + tests.Count + 2 - 1, 2],
                summary.Cells[2 + tests.Count + 2 - 1, 2 + runs.Count]];

            Range allDataBaseline = summary.Range[
                summary.Cells[3 + tests.Count + 3 - 1, 2],
                summary.Cells[3 + tests.Count + tests.Count + 3 - 2, 2]];

            Range allDataRuns = summary.Range[
                summary.Cells[3 + tests.Count + 3 - 1, 3],
                summary.Cells[3 + tests.Count + tests.Count + 3 - 2, 3 + runs.Count - 1]];

            SheetHelper.ExcelCall(() =>
            {
                allComparisonHeaders.FormatConditions.Delete();
                allComparisonBaseline.FormatConditions.Delete();
                allComparisonRuns.FormatConditions.Delete();
                allDataHeaders.FormatConditions.Delete();
                allDataBaseline.FormatConditions.Delete();
                allDataRuns.FormatConditions.Delete();
            });

            int index = 0;
            foreach (var pair in tests)
            {
                string testName = pair.Key;
                Dictionary<string, float> testData = pair.Value;

                SheetHelper.ExcelCall(() =>
                {
                    summary.Cells[allComparisonBaseline.Row + index, "A"] = testName;
                    summary.Cells[allDataBaseline.Row + index, "A"] = testName;
                    if (baselineName != null && testData.ContainsKey(baselineName))
                        summary.Cells[allDataBaseline.Row + index, allDataBaseline.Column] = testData[baselineName];
                });

                for (int i = 0; i < runs.Count; i++)
                {
                    SheetHelper.ExcelCall(() =>
                    {
                        if (!testData.ContainsKey(runs[i]))
                        {
                            summary.Cells[allDataRuns.Row + index, allDataRuns.Column + i] = null;
                            summary.Cells[allComparisonRuns.Row + index, allComparisonRuns.Column + i] = null;
                        }
                        else
                        {
                            summary.Cells[allDataRuns.Row + index, allDataRuns.Column + i] = testData[runs[i]];
                            if (testData.ContainsKey(baselineName))
                                summary.Cells[allComparisonRuns.Row + index, allComparisonRuns.Column + i] = testData[runs[i]] / testData[baselineName];
                            else
                                summary.Cells[allComparisonRuns.Row + index, allComparisonRuns.Column + i] = null;
                        }
                    });
                }
                index++;
            }

            SheetHelper.ExcelCall(() =>
            {
                allComparisonHeaders.Style = workbook.Styles["Header"];
                allDataHeaders.Style = workbook.Styles["Header"];
                allComparisonRuns.Style = workbook.Styles["Comparison"];
                allDataBaseline.Style = workbook.Styles["Data"];
                allComparisonBaseline.Style = workbook.Styles["Data"];
                allDataRuns.Style = workbook.Styles["Data"];
            });
            SheetHelper.ApplyColorFormatting(allComparisonRuns, colorThresholdLow, colorThresholdHigh);
        }



        public void SetBaseline(string from)
        {
            if (!SheetExists(from))
            {
                Console.Out.WriteLine("Specified baseline build {0} does not exist.  Setting an arbitrary baseline.", from);
                from = builds[0];
                //return;
            }

            Console.Out.WriteLine("Setting baseline: Build {0}", from);

            Microsoft.Office.Interop.Excel.Worksheet baselineSheet = workbook.Sheets[from];

            foreach (Microsoft.Office.Interop.Excel.Worksheet sheet in workbook.Sheets)
                UpdateSheet(sheet.Name, from);

            UpdateSummary(from);
        }

        public bool SheetExists(string name)
        {
            foreach (Worksheet existing in workbook.Sheets)
                if (existing.Name == name)
                    return true;
            return false;
        }



        public static string CoordsToString(int x, int y)
        {
            // First, generate the column name.
            // It starts with A .. Z, then goes to AA .. AZ.
            // Not quite base 26, because the tens place starts blank.
            int tensPlace = x / 26;
            int onesPlace = x % 26;

            string output = ((char)('A' + onesPlace)).ToString();
            if (tensPlace > 0)
                output = ((char)('A' + (tensPlace - 1))).ToString() + output;

            // Next, add the row number.
            output += y;

            return output;
        }
    }
}
