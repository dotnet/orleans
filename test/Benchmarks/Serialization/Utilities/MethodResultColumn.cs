using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using System.Reflection;

namespace Benchmarks.Utilities
{
    public class MethodResultColumn : IColumn
    {
        private readonly Func<object, string> _formatter;

        public MethodResultColumn(string columnName, Func<object, string> formatter, string legend = null)
        {
            ColumnName = columnName;
            _formatter = formatter;
            Legend = legend;
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, null);

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => _formatter(CallMethod(benchmarkCase));

        private static object CallMethod(BenchmarkCase benchmarkCase)
        {
            try
            {
                var descriptor = benchmarkCase.Descriptor;
                var instance = Activator.CreateInstance(descriptor.Type);
                TryInvoke(instance, descriptor.GlobalSetupMethod);
                TryInvoke(instance, descriptor.IterationSetupMethod);
                var result = descriptor.WorkloadMethod.Invoke(instance, Array.Empty<object>());
                TryInvoke(instance, descriptor.IterationCleanupMethod);
                TryInvoke(instance, descriptor.GlobalCleanupMethod);

                return result;

                static void TryInvoke(object target, MethodInfo method)
                {
                    try
                    {
                        _ = (method?.Invoke(target, Array.Empty<object>()));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

        public bool IsAvailable(Summary summary) => summary.Reports.Any(r => CallMethod(r.BenchmarkCase) != null);

        public string Id => nameof(MethodResultColumn) + "_" + ColumnName;
        public string ColumnName { get; }
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Metric;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Size;
        public string Legend { get; }
    }
}