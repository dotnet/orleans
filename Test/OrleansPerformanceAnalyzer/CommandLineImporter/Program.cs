using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
//using OrleansPerformanceData;
using System.Diagnostics;
using Microsoft.Office.Interop;
using Microsoft.Office.Interop.Excel;
using OrleansPerformanceData;
//using Microsoft.Office.Tools.Excel;


namespace OrleansPerformanceData
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                DisplayUsage();
                return;
            }

            string allArgs = args[0];
            for (int i = 1; i < args.Length; i++)
                allArgs += " " + args[i];

            string[] switches = allArgs.Split(new char[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
            
            string bulkImportDir = null;
            string outputPath = null;
            string desiredBaseline = null;
            string desiredReport = null;

            string colorThresholdLowString = "75%";
            string colorThresholdHighString = "133%";


            foreach (string arg in switches)
            {
                if (arg.IndexOf(':') < 0)
                {
                    string name = arg.ToLower();
                }
                else
                {
                    string name = arg.Substring(0, arg.IndexOf(':')).ToLower().Trim();
                    string value = arg.Substring(arg.IndexOf(':') + 1).Trim();

                    switch (name)
                    {
                        case "bulk":
                            bulkImportDir = value;
                            if (!bulkImportDir.EndsWith(@"\"))
                                bulkImportDir += @"\";
                            break;
                        case "output":
                            outputPath = value;
                            if (!outputPath.EndsWith(".xlsm"))
                                outputPath += ".xlsm";
                            break;
                        case "baseline":
                            desiredBaseline = value;
                            break;
                        case "report":
                            desiredReport = value;
                            break;
                        case "colorthresholdlow":
                            colorThresholdLowString = value;
                            break;
                        case "colorthresholdhigh":
                            colorThresholdHighString = value;
                            break;
                    }
                }
            }

            if (bulkImportDir == null || outputPath == null)
            {
                DisplayUsage();
                return;
            }

            bulkImportDir = System.IO.Path.GetFullPath(bulkImportDir);
            outputPath = System.IO.Path.GetFullPath(outputPath);


            #region Calculate the color thresholds
            if (colorThresholdLowString.EndsWith("%"))
                colorThresholdLowString = colorThresholdLowString.Substring(0, colorThresholdLowString.Length - 1);
            if (colorThresholdHighString.EndsWith("%"))
                colorThresholdHighString = colorThresholdHighString.Substring(0, colorThresholdHighString.Length - 1);
            float colorThresholdLow, colorThresholdHigh;
            if (
                float.TryParse(colorThresholdLowString, out colorThresholdLow) &&
                float.TryParse(colorThresholdHighString, out colorThresholdHigh) &&
                colorThresholdLow < colorThresholdHigh
                )
            {
                colorThresholdLow /= 100f;
                colorThresholdHigh /= 100f;
            }
            else
            {
                Console.Out.WriteLine("Invalid color bounds.  Try a format like 90.5%, and make sure that");
                Console.Out.WriteLine("the low bound is below the high bound.");
                return;
            }
            #endregion


            Console.Out.WriteLine();
            Console.Out.WriteLine("Loading from directory {0} ...", bulkImportDir);
            Console.Out.WriteLine("Output file is {0} ...", outputPath);
            Console.Out.WriteLine("Color thresholds are red at {0}% and green at {1}% ...", colorThresholdLow * 100f, colorThresholdHigh * 100f);
            Console.Out.WriteLine();

            // Excel isn't very smart.  Acquire access, then release it just before Excel writes to it.  There's probably a better way to do this.
            System.IO.FileStream fileAccess;
            try
            {
                fileAccess = System.IO.File.OpenWrite(outputPath);
            }
            catch (System.IO.IOException)
            {
                Console.Out.WriteLine("Invalid filename or file in use.  Please close the file if it is open, or specify a different filename.");
                return;
            }


            ReportHandler reportHandler = new ReportHandler(System.IO.Path.GetFullPath(@".\reportConfiguration.xml"));

            Microsoft.Office.Interop.Excel.Application app = new Microsoft.Office.Interop.Excel.Application();

            app.Workbooks.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), @".\ImporterWorkbook.xlsm"));
            //app.Workbooks.Add(System.IO.Path.GetFullPath(@".\ImporterWorkbook.xlsm"));
            //app.Workbooks.Add(System.IO.Path.GetFullPath(@".\OrleansPerformanceData.xlsx"));
            //app.Workbooks.Add(System.IO.Path.GetFullPath(@".\macros.xltm"));
            app.Visible = false;

            Workbook workbook = app.Workbooks[1];

            DataHandler dataHandler = new DataHandler(workbook);
            dataHandler.SetColorThresholds(colorThresholdLow, colorThresholdHigh);

            dataHandler.BulkLoad(bulkImportDir);

            if (dataHandler.builds.Count < 1)
            {
                Console.Out.WriteLine("No builds were found!  Quitting...");
                return;
            }

            if (desiredBaseline == null)
            {
                dataHandler.SetBaseline(dataHandler.builds[0]);
            }
            else
            {
                // Check to see if it's valid
                dataHandler.SetBaseline(desiredBaseline);
            }

            CategorySheetHandler categoryHandler = new CategorySheetHandler();
            categoryHandler.CreateSheet(app);
            categoryHandler.PopulateSheet(dataHandler);

            Console.Out.WriteLine("Saving...");

            try
            {
                fileAccess.Close();
                workbook.SaveCopyAs(outputPath);
            }
            catch (System.IO.IOException)
            {
                Console.Out.WriteLine("An error occurred while writing the file.");
            }

            if (desiredReport != null && dataHandler.builds.Contains(desiredReport) &&
                desiredBaseline != null && dataHandler.builds.Contains(desiredBaseline))
            {
                Console.Out.WriteLine("Generating report...");
                reportHandler.Begin();
                foreach (var pair in dataHandler.GenerateCategoryAverageComparisons(desiredBaseline, desiredReport))
                {
                    reportHandler.ReportResult(pair.Key, pair.Value);
                }
                reportHandler.End();
            }
            else
            {
                Console.Out.WriteLine("No data for additional report summary.");
            }



            Console.Out.WriteLine("Done.");
        }

        public static void DisplayUsage()
        {
            Console.Out.WriteLine(@"USAGE:");
            Console.Out.WriteLine(@"importer /bulk:path\to\directory /output:location\of\output.xlsm");
            Console.Out.WriteLine(@"Optional flags:");
            Console.Out.WriteLine(@"    /baseline:12345");
            Console.Out.WriteLine(@"The path should point to a directory which contains subdirectories (at any distance down the tree)");
            Console.Out.WriteLine(@"which contain performanceLog.xml files.  The name of the subdirectory that immediately contains");
            Console.Out.WriteLine(@"each one is considered to be its build number.");
        }
    }
}
