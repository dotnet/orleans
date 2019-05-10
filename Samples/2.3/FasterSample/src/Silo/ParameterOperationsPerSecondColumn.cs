using System;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Silo
{
    public class ParameterOperationsPerSecondColumn : IColumn
    {
        public ParameterOperationsPerSecondColumn(string countColumnName, string columnName = null, string legend = null)
        {
            CountColumnName = countColumnName ?? throw new ArgumentNullException(nameof(countColumnName));
            ColumnName = columnName ?? $"Calls/sec @ ({CountColumnName})";
            Legend = legend;
        }

        #region Configuration

        public string CountColumnName { get; }

        #endregion

        public string Id => nameof(ParameterOperationsPerSecondColumn);

        public string ColumnName { get; }

        public bool AlwaysShow => true;

        public ColumnCategory Category => ColumnCategory.Custom;

        public int PriorityInCategory => 0;

        public bool IsNumeric => true;

        public UnitType UnitType => UnitType.Dimensionless;

        public string Legend { get; }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        {
            var count = Convert.ToDouble(benchmarkCase.Parameters[CountColumnName]);
            var value = summary[benchmarkCase].AllMeasurements
                .Where(_ => _.IterationMode == IterationMode.Workload)
                .Select(_ => (count / _.Nanoseconds) * 1000.0 * 1000.0 * 1000.0)
                .Average();
            return value.ToString();
        }

        public bool IsAvailable(Summary summary) => true;

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    }
}
