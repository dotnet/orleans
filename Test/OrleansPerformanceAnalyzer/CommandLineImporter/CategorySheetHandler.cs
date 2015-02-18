using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Office.Interop.Excel;
using System.Reflection;

using MSForms = Microsoft.Vbe.Interop.Forms;


namespace OrleansPerformanceData
{
    public class CategorySheetHandler
    {
        
        Application app;
        Worksheet categorySheet;
        Workbook workbook;
        public Dictionary<string, Dictionary<string, float>> categoryAverages; // Outer string: build ID, inner string: category name

        public void CreateSheet(Application app)
        {
            this.app = app;
            workbook = app.Workbooks[1];
            workbook.Sheets.Add(Type.Missing, Type.Missing, Type.Missing, Type.Missing);
            categorySheet = workbook.ActiveSheet;
            categorySheet.Name = "Categories";
            app.Run("SetBaselineComparisonColumn", 10);
            categoryAverages = new Dictionary<string, Dictionary<string, float>>();
        }

        public void PopulateSheet(DataHandler dataHandler)
        {
            ((Range)(categorySheet.Rows[1])).RowHeight = 24;

            PopulateData(dataHandler);
            PopulateComparison(dataHandler);

            foreach (Range r in categorySheet.Columns)
                r.AutoFit();
        }

        public void Dummy() {
            categorySheet.Cells[1, 1] = 12345;
        }

        public void PopulateComparison(DataHandler dataHandler)
        {
            int xOffset = 0, yOffset = 0;

            // First row: Build numbers
            for (int buildIndex = 0; buildIndex < dataHandler.builds.Count; buildIndex++)
            {
                categorySheet.Cells[1, 3 + buildIndex] = dataHandler.builds[buildIndex];
                /*
                Shape selectAsBaseline = categorySheet.Shapes.AddOLEObject("Forms.CommandButton.1",
                    Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, 0, 0, 100, 40);
                selectAsBaseline.Name = "Button" + buildIndex;
                selectAsBaseline.Title = dataHandler.builds[buildIndex];
                 * */
                
                /*
                var buttonSelectAsBaseline = categorySheet.Shapes.AddOLEObject(ClassType: "Forms.CommandButton.1");
                //buttonSelectAsBaseline.OnAction = "";
                buttonSelectAsBaseline.Left = (float)((Range)(categorySheet.Cells[1, 3 + buildIndex])).Left;
                buttonSelectAsBaseline.Top = (float)((Range)(categorySheet.Cells[1, 3 + buildIndex])).Top;
                buttonSelectAsBaseline.Width = (float)((Range)(categorySheet.Cells[1, 3 + buildIndex])).Width;
                buttonSelectAsBaseline.Height = (float)((Range)(categorySheet.Cells[1, 3 + buildIndex])).Height;
                buttonSelectAsBaseline.Placement = XlPlacement.xlMoveAndSize;
                buttonSelectAsBaseline.Name = "buttonSelectBaselineColumn" + buildIndex;
                
                OLEObject button = categorySheet.OLEObjects("buttonSelectBaselineColumn" + buildIndex);
                button.Object.Caption = dataHandler.builds[buildIndex];

                //button.Object.OnAction = "Dummy";
                //button.Object.Click += new Microsoft.Vbe.Interop.Forms.CommandButtonEvents_ClickEventHandler(Dummy);
                //button.Object.OnAction = string.Format("SetBaselineComparisonColumn {0}", (3 + buildIndex));
                //button.Object.ClickHandler = null;
                //buttonSelectAsBaseline.OnAction = string.Format("'SetBaselineComparisonColumn {0}'", (3 + buildIndex));
                 * */
                /*
                CommandButton b = (MSForms.CommandButton)buttonSelectAsBaseline;
                b.Caption = dataHandler.builds[buildIndex];
                 * */
            }


            // Display each category with a single row space below the previous one
            int nextCategoryStartRow = 2;

            for (int i = 0; i < dataHandler.categoryNames.Count; i++)
            {
                object[,] categoryOutput = BuildComparisonOutputArray(dataHandler.categoryNames[i], dataHandler);
                for (int x = 0; x < categoryOutput.GetLength(0); x++)
                    for (int y = 0; y < categoryOutput.GetLength(1); y++)
                    {
                        if (categoryOutput[x, y] != null)
                        {
                            //Console.Out.WriteLine(ParseString(categoryOutput[x, y].ToString(), 0, 3 + dataHandler.builds.Count, yOffset + nextCategoryStartRow));
                            categorySheet.Cells[yOffset + y + nextCategoryStartRow, xOffset + x + 1] = ParseString(categoryOutput[x, y].ToString(), 0, 3 + dataHandler.builds.Count, yOffset + nextCategoryStartRow);
                        }
                        else
                        {
                            categorySheet.Cells[yOffset + y + nextCategoryStartRow, xOffset + x + 1] = null;
                        }
                    }



                // This is where the comparison data lives...
                Range comparisonRange = categorySheet.Range[
                    categorySheet.Cells[yOffset + nextCategoryStartRow, xOffset + 1],
                    categorySheet.Cells[yOffset + nextCategoryStartRow + categoryOutput.GetLength(1), xOffset + 1 + categoryOutput.GetLength(0)]];
                SheetHelper.ApplyColorFormatting(comparisonRange, dataHandler.colorThresholdLow, dataHandler.colorThresholdHigh);
                comparisonRange.Style = workbook.Styles["Comparison"];

                /*
                Range averageRange = categorySheet.Range[
                    categorySheet.Cells[yOffset + nextCategoryStartRow + categoryOutput.GetLength(1) - 1, xOffset + 3],
                    categorySheet.Cells[yOffset + nextCategoryStartRow + categoryOutput.GetLength(1) - 1, xOffset + 3 + categoryOutput.GetLength(0) - 1]];
                CreateComparisonChart(averageRange);
                 * */

                nextCategoryStartRow += categoryOutput.GetLength(1) + 2;
            }

        }

        public void PopulateData(DataHandler dataHandler)
        {
            // Offset this all by the width of the comparison!
            // See below: 1 for category name, 1 for test name, N for builds, and an extra 1 for spacing
            int xOffset = 3 + dataHandler.builds.Count, yOffset = 0;

            // First row: Build numbers
            for (int buildIndex = 0; buildIndex < dataHandler.builds.Count; buildIndex++)
                categorySheet.Cells[yOffset + 3 + buildIndex, xOffset + 1] = dataHandler.builds[buildIndex];

            // Display each category with a single row space below the previous one
            int nextCategoryStartRow = 2;

            for (int i = 0; i < dataHandler.categoryNames.Count; i++)
            {
                object[,] categoryOutput = BuildCategoryOutputArray(dataHandler.categoryNames[i], dataHandler);
                for (int x = 0; x < categoryOutput.GetLength(0); x++)
                    for (int y = 0; y < categoryOutput.GetLength(1); y++)
                        //categorySheet.Cells[yOffset + y + nextCategoryStartRow, xOffset + x + 1] = categoryOutput[x, y];
                    {
                        if (categoryOutput[x, y] != null)
                        {
                            categorySheet.Cells[yOffset + y + nextCategoryStartRow, xOffset + x + 1] = ParseString(categoryOutput[x, y].ToString(), 0, 3 + dataHandler.builds.Count, yOffset + nextCategoryStartRow);
                        }
                        else
                        {
                            categorySheet.Cells[yOffset + y + nextCategoryStartRow, xOffset + x + 1] = null;
                        }
                    }

                Range dataRange = categorySheet.Range[
                    categorySheet.Cells[yOffset + nextCategoryStartRow, xOffset + 1],
                    categorySheet.Cells[yOffset + nextCategoryStartRow + categoryOutput.GetLength(1), xOffset + 1 + categoryOutput.GetLength(0)]];
                dataRange.Style = workbook.Styles["Data"];

                nextCategoryStartRow += categoryOutput.GetLength(1) + 2;
            }
            foreach (Range r in categorySheet.Columns)
                r.AutoFit();
        }

        /*
        public void CreateComparisonChart(Range r)
        {
            Chart chart = (Chart)workbook.Charts.Add(Missing.Value, Missing.Value, Missing.Value, Missing.Value);
            chart.ChartType = XlChartType.xlLineMarkers;
            chart.SetSourceData(r, XlRowCol.xlColumns);
            Console.Out.WriteLine(r.ToString());
            Chart embeddedChart = chart.Location(XlChartLocation.xlLocationAsObject, categorySheet);
        }
         * */

        // Generate it here, so we know the size, then return it to something that will put it in the sheet
        public object[,] BuildComparisonOutputArray(string categoryName, DataHandler dataHandler)
        {
            // First column:  Name of the category
            // Second column:  Tests in the catergory
            // Third through N columns: Each build
            // Not included:  Column labels with build IDs
            // Included: Columns with no entries
            // Rows are equal to number of tests in the category + 1
            // Last row is the category average
            List<string> testNames = dataHandler.categoryMembers[categoryName];
            object[,] output = new object[dataHandler.builds.Count + 2, testNames.Count + 1];

            // Name of the category
            output[0, 0] = categoryName;
            for (int y = 1; y < testNames.Count(); y++)
                output[0, y] = null;

            // Test names
            for (int test = 0; test < testNames.Count(); test++)
                output[1, test] = testNames[test];
            output[1, testNames.Count] = "AVERAGE";

            // Results
            for (int buildIndex = 0; buildIndex < dataHandler.builds.Count(); buildIndex++)
            {
                string buildId = dataHandler.builds[buildIndex];
                for (int testNum = 0; testNum < testNames.Count; testNum++)
                {
                    Dictionary<string, float> buildTestResults = dataHandler.tests[testNames[testNum]];
                    float baselineTestResults = dataHandler.tests[testNames[testNum]][dataHandler.baseline];
                    if (buildTestResults.ContainsKey(buildId))
                    {
                        if (buildTestResults[buildId] > 0 && baselineTestResults > 0)
                        {
                            //output[2 + buildIndex, testNum] = buildTestResults[buildId] / baselineTestResults;
                            //output[2 + buildIndex, testNum] = string.Format("=#DATA,{0},{1}#/#DATA,{2},{3}#", buildIndex + 2, testNum, 2, testNum);
                            output[2 + buildIndex, testNum] = string.Format("=CompareWithBaseline(#OFFSET,{0}#, {1}, {2})", testNum, buildIndex + 1, dataHandler.builds.Count);
                            //Console.Out.WriteLine(output[2+buildIndex, testNum]);
                        }
                    }
                    else
                        output[2 + buildIndex, testNum] = null;
                }
                output[2 + buildIndex, testNames.Count] = string.Format("=AVERAGE(#COMPARISON,{0},{1}#:#COMPARISON,{2},{3}#)", buildIndex + 2, 0, buildIndex + 2, testNames.Count - 1);
            }

            return output;
        }

        // Generate it here, so we know the size, then return it to something that will put it in the sheet
        public object[,] BuildCategoryOutputArray(string categoryName, DataHandler dataHandler)
        {
            // First column:  Name of the category
            // Second column:  Tests in the catergory
            // Third through N columns: Each build
            // Not included:  Column labels with build IDs
            // Included: Columns with no entries
            // Rows are equal to number of tests in the category + 1
            // Last row is the category average
            List<string> testNames = dataHandler.categoryMembers[categoryName];
            object[,] output = new object[dataHandler.builds.Count + 2, testNames.Count + 1];

            // Name of the category
            output[0, 0] = categoryName;
            for (int y = 1; y < testNames.Count(); y++)
                output[0, y] = null;

            // Test names
            for (int test = 0; test < testNames.Count(); test++)
                output[1, test] = testNames[test];
            output[1, testNames.Count] = "AVERAGE";

            // Results
            for (int buildIndex = 0; buildIndex < dataHandler.builds.Count(); buildIndex++)
            {
                string buildId = dataHandler.builds[buildIndex];
                for (int testNum = 0; testNum < testNames.Count; testNum++)
                {
                    Dictionary<string, float> buildTestResults = dataHandler.tests[testNames[testNum]];
                    if (buildTestResults.ContainsKey(buildId))
                    {
                        output[2 + buildIndex, testNum] = buildTestResults[buildId];
                    }
                    else
                        output[2 + buildIndex, testNum] = null;
                }
                output[2 + buildIndex, testNames.Count] = string.Format("=AVERAGE(#DATA,{0},{1}#:#DATA,{2},{3}#)", buildIndex + 2, 0, buildIndex + 2, testNames.Count - 1);
            }

            return output;
        }


        private static string ParseString(string input, int comparisonOffsetX, int dataOffsetX, int offsetY)
        {
            string output = "";

            int nextIndex = input.IndexOf('#');
            while (nextIndex >= 0)
            {
                output += input.Substring(0, nextIndex);
                //Console.Out.WriteLine("Output: " + output);

                int secondIndex = input.Substring(nextIndex + 1).IndexOf('#');
                if (secondIndex < 0)
                    return output;

                string symbol = input.Substring(nextIndex + 1, secondIndex);
                //Console.Out.WriteLine("Symbol: " + symbol);

                string[] symbols = symbol.Split(new char[] {','}, StringSplitOptions.RemoveEmptyEntries);

                if (symbols[0].ToLower() == "data")
                {
                    output += DataHandler.CoordsToString(int.Parse(symbols[1]) + dataOffsetX, int.Parse(symbols[2]) + offsetY);
                    //Console.Out.WriteLine("Output: " + output);
                }
                if (symbols[0].ToLower() == "comparison")
                {
                    output += DataHandler.CoordsToString(int.Parse(symbols[1]) + comparisonOffsetX, int.Parse(symbols[2]) + offsetY);
                    //Console.Out.WriteLine("Output: " + output);
                }
                if (symbols[0].ToLower() == "offset")
                {
                    output += int.Parse(symbols[1]) + offsetY;
                }
                //Console.Out.WriteLine("nextIndex {0} secondIndex {1}", nextIndex, secondIndex);
                input = input.Substring(nextIndex + secondIndex + 2);
                //Console.Out.WriteLine("Input: " + input);

                nextIndex = input.IndexOf('#');
            }
            return output + input;
        }
    }
}
