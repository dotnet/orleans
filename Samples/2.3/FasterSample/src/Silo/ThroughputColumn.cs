using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Silo
{
    public class ThroughputColumn : IColumn
    {
        public ThroughputColumn(string columnName, string countColumnName, string legend = null)
        {
            ColumnName = columnName ?? throw new ArgumentNullException(nameof(ColumnName));
            CountColumnName = countColumnName ?? throw new ArgumentNullException(nameof(countColumnName));
            Legend = legend;
        }

        public string Id => nameof(ThroughputColumn);

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
            dynamic count = summary.GetColumns().OfType<ParamColumn>().Where(_ => _.ColumnName == ColumnName).SingleOrDefault();

            var report = summary[benchmarkCase];

            var opsPerSecond = report.AllMeasurements.Select(_ => (10000.0 / _.Nanoseconds) * 1000.0 * 1000.0 * 1000.0);

            return null;
        }

        public bool IsAvailable(Summary summary)
        {
            return true;
        }

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
        {
            return false;
        }

        #region

        public string CountColumnName { get; }

        #endregion
    }
}
