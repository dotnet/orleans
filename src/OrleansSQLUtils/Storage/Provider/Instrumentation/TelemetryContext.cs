using System;
using System.Diagnostics;
using Orleans.SqlUtils.StorageProvider.Instrumentation;

namespace Orleans.SqlUtils.StorageProvider.Instrumentation
{
    public class TelemetryContext
    {
        private const string CategoryName = "Orleans SQL Storage Provider";
        public static WritablePerformanceCounter OpenConnections { get; private set; }
        public static WritablePerformanceCounter WritesOutstanding { get; private set; }
        public static WritablePerformanceCounter WritesTotal { get; private set; }
        public static WritablePerformanceCounter WritesPostedTotal { get; private set; }
        public static WritablePerformanceCounter WritesPostedFailures { get; private set; }
        public static WritablePerformanceCounter SqlTransientErrors { get; private set; }
        

        static TelemetryContext()
        {
            Console.WriteLine("Creating telemetry context");
            OpenConnections = new WritablePerformanceCounter(CategoryName, "Open Connections");
            WritesOutstanding = new WritablePerformanceCounter(CategoryName, "Writes Outstanding");
            WritesTotal = new WritablePerformanceCounter(CategoryName, "Writes Total");
            WritesPostedTotal = new WritablePerformanceCounter(CategoryName, "Writes Posted Total");
            WritesPostedFailures = new WritablePerformanceCounter(CategoryName, "Writes Posted Failures");
            SqlTransientErrors = new WritablePerformanceCounter(CategoryName, "Sql Transient Errors");
        }


        public static void Create()
        {
            if (PerformanceCounterCategory.Exists(CategoryName))
                PerformanceCounterCategory.Delete(CategoryName);

            CounterCreationDataCollection ccdc = new CounterCreationDataCollection();
            ccdc.Add(
                new CounterCreationData()
                {
                    CounterType = PerformanceCounterType.NumberOfItems32,
                    CounterName = "Open Connections",
                    CounterHelp = string.Empty
                });
            ccdc.Add(
                new CounterCreationData()
                {
                    CounterType = PerformanceCounterType.NumberOfItems32,
                    CounterName = "Writes Outstanding",
                    CounterHelp = string.Empty
                });
            ccdc.Add(
                new CounterCreationData()
                {
                    CounterType = PerformanceCounterType.NumberOfItems32,
                    CounterName = "Writes Total",
                    CounterHelp = string.Empty
                });
            ccdc.Add(
                new CounterCreationData()
                {
                    CounterType = PerformanceCounterType.NumberOfItems32,
                    CounterName = "Writes Posted Total",
                    CounterHelp = string.Empty
                });
            ccdc.Add(
                new CounterCreationData()
                {
                    CounterType = PerformanceCounterType.NumberOfItems32,
                    CounterName = "Writes Posted Failures",
                    CounterHelp = string.Empty
                });
            ccdc.Add(
                new CounterCreationData()
                {
                    CounterType = PerformanceCounterType.NumberOfItems32,
                    CounterName = "Sql Transient Errors",
                    CounterHelp = string.Empty
                });

            // Create the category.
            PerformanceCounterCategory.Create(CategoryName, string.Empty, PerformanceCounterCategoryType.SingleInstance, ccdc);
        }

        public static void Reset()
        {
            OpenConnections.ResetCounter();
            WritesOutstanding.ResetCounter();
            WritesTotal.ResetCounter();
            WritesPostedTotal.ResetCounter();
            WritesPostedFailures.ResetCounter();
            SqlTransientErrors.ResetCounter();
        }
    }
}