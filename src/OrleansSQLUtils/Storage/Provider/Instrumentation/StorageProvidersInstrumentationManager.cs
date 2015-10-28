using System;
using System.Diagnostics;
using Orleans.Runtime;
using Orleans.SqlUtils.StorageProvider.Instrumentation;

namespace Orleans.SqlUtils.StorageProvider.Instrumentation
{
    public class StorageProvidersInstrumentationManager : InstrumentationManager
    {
        private const string ThisCategoryName = "Orleans SQL Storage Provider";
        private readonly bool _instrumentationEnabled;

        internal PerformanceCounterDefinition OpenConnections { get; private set; }
        internal PerformanceCounterDefinition WritesPending { get; private set; }
        internal PerformanceCounterDefinition WritesPostFailures { get; private set; }
        internal PerformanceCounterDefinition WriteErrors { get; private set; }
        internal PerformanceCounterDefinition ReadsPending { get; private set; }
        internal PerformanceCounterDefinition ReadPostFailures { get; private set; }
        internal PerformanceCounterDefinition ReadErrors { get; private set; }
        internal PerformanceCounterDefinition SqlTransientErrors { get; private set; }

        public StorageProvidersInstrumentationManager(
            bool instrumentationEnabled = false,
            bool installInstrumentation = false)
            : base(ThisCategoryName, "", PerformanceCounterCategoryType.SingleInstance)
        {
            _instrumentationEnabled = instrumentationEnabled;

            OpenConnections = AddDefinition(
                "Open Connections",
                "Open Connections",
                PerformanceCounterType.NumberOfItems64);
            WritesPending = AddDefinition(
                "Writes Pending",
                "Writes Pending",
                PerformanceCounterType.NumberOfItems64);
            WriteErrors = AddDefinition(
                "Write Errors",
                "Write Errors",
                PerformanceCounterType.NumberOfItems64);
            ReadsPending = AddDefinition(
                "Reads Pending",
                "Reads Pending",
                PerformanceCounterType.NumberOfItems64);
            ReadErrors = AddDefinition(
                "Read Errors",
                "Read Errors",
                PerformanceCounterType.NumberOfItems64);
            WritesPostFailures = AddDefinition(
               "Writes Post Failures",
               "Writes Post Failures",
               PerformanceCounterType.NumberOfItems64);
            ReadPostFailures = AddDefinition(
               "Reads Post Failures",
               "Reads Post Failures",
               PerformanceCounterType.NumberOfItems64);
            SqlTransientErrors = AddDefinition(
               "Sql Transient Errors",
               "Sql Transient Errors",
               PerformanceCounterType.NumberOfItems64);

            if (installInstrumentation)
                CreateCounters();
        }

    }
}
