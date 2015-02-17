using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace OrleansPerformanceData
{
    public class ReportHandler
    {
        float defaultThresholdWarning = 1.00033f;
        float defaultThresholdImprovement = 0.99975f;
        Dictionary<string, float> thresholdWarningDict;
        Dictionary<string, float> thresholdImprovementDict;
        string reportFilename;
        TextWriter reportWriter;

        public ReportHandler(string configurationFile)
        {
            thresholdWarningDict = new Dictionary<string, float>();
            thresholdImprovementDict = new Dictionary<string, float>();

            ReadConfiguration(configurationFile);
        }

        public void ReadConfiguration(string configurationFile)
        {
            reportFilename = "report.txt";
        }

        public void Begin()
        {
            reportWriter = new StreamWriter(reportFilename);
            reportWriter.WriteLine("Report begins...");
        }

        public void ReportResult(string type, float value)
        {
            float thresholdWarning = (thresholdWarningDict.ContainsKey(type)) ? thresholdWarningDict[type] : defaultThresholdWarning;
            float thresholdImprovement = (thresholdImprovementDict.ContainsKey(type)) ? thresholdImprovementDict[type] : defaultThresholdImprovement;

            if (value >= thresholdWarning)
            {
                reportWriter.WriteLine("WARNING: {0} performance took {1:F2}% of the baseline duration", type, value * 100);
            }
            else if (value <= thresholdImprovement)
            {
                reportWriter.WriteLine("IMPROVEMENT: {0} performance took {1:F2}% of the baseline duration", type, value * 100);
            }
            
        }

        public void End()
        {
            reportWriter.WriteLine("Report ends");
            reportWriter.Close();
        }
    }
}
