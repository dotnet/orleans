using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using OfficeOpenXml;
using System.IO;
using System.Data.SqlClient;
using OfficeOpenXml.Drawing;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.Style;
using System.Drawing;

namespace ExcelGenerator
{

    [Serializable]
    class PlotMetrics
    {
        public DateTime DateTime;

        public long ClientCount;

        public int ActivationCount;

        public int RecentlyUsedActivationCount;

        public float CpuUsage;

        public bool IsOverloaded;

        public float AvailableMemory;

        public long TotalPhysicalMemory;

        public long MemoryUsage;

        public long RequestQueueLength;

        public int SendQueueLength;

        public int ReceiveQueueLength;

        public bool ReducePlacementRate = false;
    }

    enum StatsRowOffsets
    {
        SAMPLE_TIME,
        REQUEST_QUEUE_LENGTH,
        ACTIVATION_COUNT,
        RECENT_ACTIVATION_COUNT,
        CLIENT_COUNT,
        IS_OVERLOADED,
        CPU_USAGE,
        AVAILABLE_MEMORY,
        TOTAL_PHYSICAL_MEMORY,
        MACHINE_MEMORY_USAGE,
        MEMORY_USAGE,
        SEND_QUEUE_LENGTH,
        RECEIVE_QUEUE_LENGTH,
        REDUCE_PLACEMENT_RATE,
        END_OF_ELEMENTS // make sure this stays last
    }

    enum GlobalStatsOffsets
    {
        AVG_ACTIVATIONS,
        AVG_ACTIVATIONS_PERCENTAGE,
        MIN_ACTIVATIONS,
        MIN_ACTIVATIONS_PERCENTAGE,
        MAX_ACTIVATIONS,
        MAX_ACTIVATIONS_PERCENTAGE,
        DIFF_ACTIVATIONS,

        AVG_RECENT_ACTIVATIONS,
        AVG_RECENT_ACTIVATIONS_PERCENTAGE,
        MIN_RECENT_ACTIVATIONS,
        MIN_RECENT_ACTIVATIONS_PERCENTAGE,
        MAX_RECENT_ACTIVATIONS,
        MAX_RECENT_ACTIVATIONS_PERCENTAGE,
        DIFF_RECENT_ACTIVATIONS,

        AVG_CPU,
        AVG_CPU_PERCENTAGE,
        MIN_CPU,
        MIN_CPU_PERCENTAGE,
        MAX_CPU,
        MAX_CPU_PERCENTAGE,
        DIFF_CPU,

        AVG_MEM,
        AVG_MEM_PERCENTAGE,
        MIN_MEM,
        MIN_MEM_PERCENTAGE,
        MAX_MEM,
        MAX_MEM_PERCENTAGE,
        DIFF_MEM,

        AVG_STIME,
        MIN_STIME,
        MAX_STIME,
        DIFF_STIME
    }

    [Serializable]
    class PlotStatsData
    {
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }

        public IDictionary<string, PlotMetrics> siloStats = new Dictionary<string, PlotMetrics>();
    }

    class ExcelSheetGenerator
    {

        static IDictionary<string, int> siloPosition = new Dictionary<string, int>();
        static IDictionary<string, string> siloName = new Dictionary<string, string>();
        static int LIST_START_ROW = 100;
        static int DESCRIPTION_ROW = LIST_START_ROW + 1;
        static int STAT_START_COL = 3;
        static int STAT_DESC_COL = 2;
        static int SILO_DESC_COL = 1;
        static int MIN_MAX_START = -1;
        static private ExcelWorksheet ws;

        public static void GenerateSiloStats(IList<PlotStatsData> stats, FileInfo template, FileInfo outputfile)
        {
            using (ExcelPackage p = new ExcelPackage(template, true))
            {
                ws = p.Workbook.Worksheets[1];


                var allKeys = stats.Select(e => e.siloStats.Keys);
                var allSilos = new List<string>();
                foreach (var keys in allKeys)
                {
                    foreach (var key in keys)
                    {
                        allSilos.Add(key);
                    }
                }
                allSilos = allSilos.Distinct().OrderBy(s => s).ToList();

                for (var idx = 1; idx <= allSilos.Count; idx++)
                {
                    siloName.Add(allSilos[idx - 1], "S" + idx.ToString());
                }

                // Set up Header
                var row = LIST_START_ROW + 2;
                foreach (string s in allSilos)
                {
                    siloPosition.Add(s, row);
                    ws.Cells[row, SILO_DESC_COL].Value = siloName[s];

                    foreach (var stat in (StatsRowOffsets[])Enum.GetValues(typeof(StatsRowOffsets)))
                    {
                        ws.Cells[row + (int)stat, STAT_DESC_COL].Value = stat.ToString();
                    }
                    row += (int)StatsRowOffsets.END_OF_ELEMENTS;
                }

                // Add some space between silo and global stats
                row += 2;

                MIN_MAX_START = row;
                foreach (var stat in (GlobalStatsOffsets[])Enum.GetValues(typeof(GlobalStatsOffsets)))
                {
                    ws.Cells[row + (int)stat, STAT_DESC_COL].Value = stat.ToString();
                }

                var col = STAT_START_COL;
                foreach (var stat in stats)
                {
                    ws.Cells[LIST_START_ROW, col].Value = (stat.Timestamp.TimeOfDay - stats[0].Timestamp.TimeOfDay).ToString(@"mm\:ss");
                    ws.Cells[LIST_START_ROW + 1, col++].Value = stat.Description;
                }

                // Forall silos at given time:
                for (var sampleStartCol = 0; sampleStartCol < stats.Count; sampleStartCol++)
                {
                    col = siloStartColumn(sampleStartCol);
                    var stat = stats[sampleStartCol];

                    var allSiloAddresses = new List<Dictionary<StatsRowOffsets, ExcelAddress>>();

                    // Write all individual silo stats for that sample:
                    foreach (var silo in stat.siloStats.Keys)
                    {
                        row = siloPosition[silo];

                        Dictionary<StatsRowOffsets, ExcelAddress> excelAddresses = calculateAddresses(row, col);
                        writeStatsAt(excelAddresses, stat.siloStats[silo]);
                        allSiloAddresses.Add(excelAddresses);
                    }

                    row = MIN_MAX_START;

                    // Write global stats for all Silos for that sample:
                    var activationCounts = allCellsFor(StatsRowOffsets.ACTIVATION_COUNT, allSiloAddresses);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_ACTIVATIONS, col].Formula = string.Format("AVERAGE({0})", activationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_ACTIVATIONS_PERCENTAGE, col].Formula = string.Format("100");
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_ACTIVATIONS, col].Formula = string.Format("MIN({0})", activationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_ACTIVATIONS_PERCENTAGE, col].Formula = string.Format("(MIN({0}) / AVERAGE({0})) * 100", activationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_ACTIVATIONS, col].Formula = string.Format("MAX({0})", activationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_ACTIVATIONS_PERCENTAGE, col].Formula = string.Format("(MAX({0}) / AVERAGE({0})) * 100", activationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.DIFF_ACTIVATIONS, col].Formula = string.Format("MAX({0}) - MIN({0})", activationCounts);

                    var recentActivationCounts = allCellsFor(StatsRowOffsets.RECENT_ACTIVATION_COUNT, allSiloAddresses);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_RECENT_ACTIVATIONS, col].Formula = string.Format("AVERAGE({0})", recentActivationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_RECENT_ACTIVATIONS_PERCENTAGE, col].Formula = string.Format("100");
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_RECENT_ACTIVATIONS, col].Formula = string.Format("MIN({0})", recentActivationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_RECENT_ACTIVATIONS_PERCENTAGE, col].Formula = string.Format("(MIN({0}) / AVERAGE({0})) * 100", recentActivationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_RECENT_ACTIVATIONS, col].Formula = string.Format("MAX({0})", recentActivationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_RECENT_ACTIVATIONS_PERCENTAGE, col].Formula = string.Format("(MAX({0}) / AVERAGE({0})) * 100", recentActivationCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.DIFF_RECENT_ACTIVATIONS, col].Formula = string.Format("MAX({0}) - MIN({0})", recentActivationCounts);

                    var cpuCounts = allCellsFor(StatsRowOffsets.CPU_USAGE, allSiloAddresses);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_CPU, col].Formula = string.Format("AVERAGE({0})", cpuCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_CPU_PERCENTAGE, col].Formula = string.Format("100");
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_CPU, col].Formula = string.Format("MIN({0})", cpuCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_CPU_PERCENTAGE, col].Formula = string.Format("(MIN({0}) / AVERAGE({0})) * 100", cpuCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_CPU, col].Formula = string.Format("MAX({0})", cpuCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_CPU_PERCENTAGE, col].Formula = string.Format("(MAX({0}) / AVERAGE({0})) * 100", cpuCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.DIFF_CPU, col].Formula = string.Format("MAX({0}) - MIN({0})", cpuCounts);

                    var memCounts = allCellsFor(StatsRowOffsets.MACHINE_MEMORY_USAGE, allSiloAddresses);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_MEM, col].Formula = string.Format("AVERAGE({0})", memCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_MEM_PERCENTAGE, col].Formula = string.Format("100");
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_MEM, col].Formula = string.Format("MIN({0})", memCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_MEM_PERCENTAGE, col].Formula = string.Format("(MIN({0}) / AVERAGE({0})) * 100", memCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_MEM, col].Formula = string.Format("MAX({0})", memCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_MEM_PERCENTAGE, col].Formula = string.Format("(MAX({0}) / AVERAGE({0})) * 100", memCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.DIFF_MEM, col].Formula = string.Format("MAX({0}) - MIN({0})", memCounts);

                    var sampleTimeCounts = allCellsFor(StatsRowOffsets.SAMPLE_TIME, allSiloAddresses);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_STIME, col].Formula = string.Format("AVERAGE({0})", sampleTimeCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.AVG_STIME, col].Style.Numberformat.Format = @"hh\:mm\:ss";
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_STIME, col].Formula = string.Format("MIN({0})", sampleTimeCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MIN_STIME, col].Style.Numberformat.Format = @"hh\:mm\:ss";
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_STIME, col].Formula = string.Format("MAX({0})", sampleTimeCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.MAX_STIME, col].Style.Numberformat.Format = @"hh\:mm\:ss";
                    ws.Cells[row + (int)GlobalStatsOffsets.DIFF_STIME, col].Formula = string.Format("MAX({0}) - MIN({0})", sampleTimeCounts);
                    ws.Cells[row + (int)GlobalStatsOffsets.DIFF_STIME, col].Style.Numberformat.Format = @"hh\:mm\:ss";

                }


                makeChartForSiloRuntimeStat("MachineMemoryUsage", "Machine Memory Usage [%]", StatsRowOffsets.MACHINE_MEMORY_USAGE, stats, allSilos);
                makeChartForSiloRuntimeStat("CpuUsage", "CPU Usage [%]", StatsRowOffsets.CPU_USAGE, stats, allSilos);
                makeChartForSiloRuntimeStat("ActivationCount", "Activations [#]", StatsRowOffsets.ACTIVATION_COUNT, stats, allSilos);
                makeChartForSiloRuntimeStat("RecentActivationCount", "Recently Used Activations [#]", StatsRowOffsets.RECENT_ACTIVATION_COUNT, stats, allSilos);
                makeChartForSiloRuntimeStat("SampleTime", "Silo Sampling Times", StatsRowOffsets.SAMPLE_TIME, stats, allSilos);
                makeChartForSiloRuntimeStat("ReducePlacementRate", "Reduce Placement Rate", StatsRowOffsets.REDUCE_PLACEMENT_RATE, stats, allSilos);

                makeChartGlobal("ActivationImbalance", "#Act Min/Max/Avg",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.MIN_ACTIVATIONS, GlobalStatsOffsets.MAX_ACTIVATIONS, GlobalStatsOffsets.AVG_ACTIVATIONS },
                    stats);
                makeChartGlobal("RecentActivationImbalance", "Recently Used #Act Min/Max/Avg",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.MIN_RECENT_ACTIVATIONS, GlobalStatsOffsets.MAX_RECENT_ACTIVATIONS, GlobalStatsOffsets.AVG_RECENT_ACTIVATIONS },
                    stats);
                makeChartGlobal("CpuImbalance", "%CPU Min/Max/Avg",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.MIN_CPU, GlobalStatsOffsets.MAX_CPU, GlobalStatsOffsets.AVG_CPU },
                    stats);
                makeChartGlobal("MemImbalance", "%MEM Min/Max/Avg",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.MIN_MEM, GlobalStatsOffsets.MAX_MEM, GlobalStatsOffsets.AVG_MEM },
                    stats);
                makeChartGlobal("SampleTimeImbalance", "Silo Sampling Times Min/Max/Avg",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.AVG_STIME, GlobalStatsOffsets.MAX_STIME, GlobalStatsOffsets.MIN_STIME },
                    stats);

                makeChartGlobal("ActivationImbalancePercentage", "%Act Min/Max/Avg",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.MIN_ACTIVATIONS_PERCENTAGE, GlobalStatsOffsets.MAX_ACTIVATIONS_PERCENTAGE },
                    stats);
                makeChartGlobal("RecentActImbalancePerc", "Recently Used %Act Min/Max/Avg",
                                    new GlobalStatsOffsets[] { GlobalStatsOffsets.MIN_RECENT_ACTIVATIONS_PERCENTAGE, GlobalStatsOffsets.MAX_RECENT_ACTIVATIONS_PERCENTAGE },
                                    stats);
                makeChartGlobal("CpuImbalancePercentage", "%CPU Min/Max/Avg",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.MIN_CPU_PERCENTAGE, GlobalStatsOffsets.MAX_CPU_PERCENTAGE },
                    stats);
                makeChartGlobal("MemImbalancePercentage", "%MEM Min/Max/Avg",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.MIN_MEM_PERCENTAGE, GlobalStatsOffsets.MAX_MEM_PERCENTAGE },
                    stats);

                makeChartGlobal("ActivationsDiff", "#Activations Max/Min Difference",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.DIFF_ACTIVATIONS },
                    stats);
                makeChartGlobal("RecentActivationsDiff", "Recently Used #Activations Max/Min Difference",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.DIFF_RECENT_ACTIVATIONS },
                    stats);
                makeChartGlobal("MemDiff", "%MEM Max/Min Difference",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.DIFF_MEM },
                    stats);
                makeChartGlobal("CpuDiff", "%CPU Max/Min Difference",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.DIFF_CPU },
                    stats);
                makeChartGlobal("SampleTimeDiff", "Silo Sampling Times Max/Min Difference",
                    new GlobalStatsOffsets[] { GlobalStatsOffsets.DIFF_STIME },
                    stats);


                Console.WriteLine("Before SaveAs");
                p.SaveAs(outputfile);
            }
        }

        private static void makeChartGlobal(string templateChartName, string chartTitle,
            GlobalStatsOffsets[] series,
            IList<PlotStatsData> stats)
        {
            ExcelChart newChart = ((ExcelChart)ws.Drawings[templateChartName]);

            foreach (GlobalStatsOffsets serie in series)
            {
                var minMaxSerie = newChart.Series.Add(
                    "'" + ws.Name + "'!" + ExcelRange.GetAddress(MIN_MAX_START + (int)serie, STAT_START_COL, MIN_MAX_START + (int)serie, STAT_START_COL + stats.Count - 1),
                    "'" + ws.Name + "'!" + ExcelRange.GetAddress(LIST_START_ROW, STAT_START_COL, LIST_START_ROW, STAT_START_COL + stats.Count - 1));
                minMaxSerie.Header = serie.ToString();
            }
            newChart.XAxis.Format = "mm:ss";
            newChart.Title.Text = chartTitle;
        }

        private static void makeChartForSiloRuntimeStat(string templateChartName, string chartTitle, StatsRowOffsets plotThis, IList<PlotStatsData> stats, List<string> allSilos)
        {
            //Get the chart drawings, they must exist in the template or the program will crash.
            ExcelChart newChart = ((ExcelChart)ws.Drawings[templateChartName]);
            foreach (var silo in allSilos)
            {
                var fromRow = siloPosition[silo] + (int)plotThis;

                var serie = newChart.Series.Add(
                    "'" + ws.Name + "'!" + ExcelRange.GetAddress(fromRow, STAT_START_COL, fromRow, STAT_START_COL + stats.Count - 1),
                    "'" + ws.Name + "'!" + ExcelRange.GetAddress(LIST_START_ROW, STAT_START_COL, LIST_START_ROW, STAT_START_COL + stats.Count - 1));
                serie.Header = siloName[silo];
            }
            newChart.XAxis.Format = "mm:ss";
            newChart.Title.Text = chartTitle;
        }

        private static string allCellsFor(StatsRowOffsets row, List<Dictionary<StatsRowOffsets, ExcelAddress>> allSiloAddresses)
        {
            return string.Join(",", allSiloAddresses.Select(dict => dict[row].Address));
        }

        private static void writeStatsAt(Dictionary<StatsRowOffsets, ExcelAddress> excelAddresses, PlotMetrics plotMetrics)
        {
            ws.Cells[excelAddresses[StatsRowOffsets.SAMPLE_TIME].Address].Formula = string.Format("=TIME({0}, {1}, {2})", plotMetrics.DateTime.Hour, plotMetrics.DateTime.Minute, plotMetrics.DateTime.Second);
            ws.Cells[excelAddresses[StatsRowOffsets.SAMPLE_TIME].Address].Style.Numberformat.Format = @"hh\:mm\:ss";
            ws.Cells[excelAddresses[StatsRowOffsets.CPU_USAGE].Address].Value = plotMetrics.CpuUsage;
            ws.Cells[excelAddresses[StatsRowOffsets.AVAILABLE_MEMORY].Address].Value = plotMetrics.AvailableMemory;
            ws.Cells[excelAddresses[StatsRowOffsets.MEMORY_USAGE].Address].Value = plotMetrics.MemoryUsage;
            ws.Cells[excelAddresses[StatsRowOffsets.TOTAL_PHYSICAL_MEMORY].Address].Value = plotMetrics.TotalPhysicalMemory;
            ws.Cells[excelAddresses[StatsRowOffsets.SEND_QUEUE_LENGTH].Address].Value = plotMetrics.SendQueueLength;
            ws.Cells[excelAddresses[StatsRowOffsets.RECEIVE_QUEUE_LENGTH].Address].Value = plotMetrics.ReceiveQueueLength;
            ws.Cells[excelAddresses[StatsRowOffsets.REQUEST_QUEUE_LENGTH].Address].Value = plotMetrics.RequestQueueLength;
            ws.Cells[excelAddresses[StatsRowOffsets.ACTIVATION_COUNT].Address].Value = plotMetrics.ActivationCount;
            ws.Cells[excelAddresses[StatsRowOffsets.RECENT_ACTIVATION_COUNT].Address].Value = plotMetrics.RecentlyUsedActivationCount;
            ws.Cells[excelAddresses[StatsRowOffsets.CLIENT_COUNT].Address].Value = plotMetrics.ClientCount;
            ws.Cells[excelAddresses[StatsRowOffsets.IS_OVERLOADED].Address].Value = plotMetrics.IsOverloaded;
            ws.Cells[excelAddresses[StatsRowOffsets.REDUCE_PLACEMENT_RATE].Address].Value = plotMetrics.ReducePlacementRate;


            ws.Cells[excelAddresses[StatsRowOffsets.MACHINE_MEMORY_USAGE].Address].Formula = string.Format("(({0} - {1}) / {0}) * 100",
                excelAddresses[StatsRowOffsets.TOTAL_PHYSICAL_MEMORY].Address,
                excelAddresses[StatsRowOffsets.AVAILABLE_MEMORY].Address);
        }

        private static Dictionary<StatsRowOffsets, ExcelAddress> calculateAddresses(int row, int col)
        {
            var excelAddresses = new Dictionary<StatsRowOffsets, ExcelAddress>();
            foreach (var stat in (StatsRowOffsets[])Enum.GetValues(typeof(StatsRowOffsets)))
            {
                excelAddresses[stat] = new ExcelAddress(row + (int)stat, col, row + (int)stat, col);
            }

            return excelAddresses;

        }

        private static int siloStartColumn(int siloNumber)
        {
            return STAT_START_COL + siloNumber;
        }

    }
}
