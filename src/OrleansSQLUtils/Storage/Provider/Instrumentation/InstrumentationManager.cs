using System;
using System.Configuration.Install;
using System.Diagnostics;
using System.Linq;
using System.Security;

namespace Orleans.SqlUtils.StorageProvider.Instrumentation
{
    public abstract class InstrumentationManager
    {
        private readonly string _categoryName;
        private readonly string _categoryHelp;
        private readonly CounterCreationDataCollection _counterDefinitions;
        private readonly PerformanceCounterCategoryType _categoryType;

        protected InstrumentationManager(string categoryName, string categoryHelp, PerformanceCounterCategoryType categoryType)
        {
            _categoryName = categoryName;
            _categoryHelp = categoryHelp;
            _categoryType = categoryType;
            _counterDefinitions = new CounterCreationDataCollection();
        }

        public string CategoryName
        {
            get { return _categoryName; }
        }

        public Installer GetInstaller()
        {
            var installer = new PerformanceCounterInstaller
            {
                CategoryName = _categoryName,
                CategoryHelp = _categoryHelp,
            };
            installer.Counters.AddRange(_counterDefinitions);

            return installer;
        }

        protected PerformanceCounterDefinition AddDefinition(string counterName, string counterHelp, PerformanceCounterType counterType)
        {
            var definition = new PerformanceCounterDefinition(_categoryName, counterName, counterHelp, counterType);

            _counterDefinitions.Add(definition.GetCreationData());

            return definition;
        }

        protected void CreateCounters()
        {
            var categoryExists = false;

            // check if all the perf counters exist - this is the best that can be done with the current API
            if ((categoryExists = PerformanceCounterCategory.Exists(_categoryName))
                && _counterDefinitions.Cast<CounterCreationData>().All(ccd => PerformanceCounterCategory.CounterExists(ccd.CounterName, _categoryName)))
            {
                return;
            }

            try
            {
                if (categoryExists)
                {
                    PerformanceCounterCategory.Delete(_categoryName);
                }

                PerformanceCounterCategory.Create(_categoryName, _categoryHelp, _categoryType, _counterDefinitions);
            }
            catch (UnauthorizedAccessException e)
            {
                Trace.TraceError("Error creating the performance counter category named '{0}'. Ensure the process is running with the necessary privileges. Exception: {1}", _categoryName, e);
            }
            catch (SecurityException e)
            {
                Trace.TraceError("Error creating the performance counter category named '{0}'. Ensure the process is running with the necessary privileges. Exception: {1}", _categoryName, e);
            }
            catch (Exception e)
            {
                Trace.TraceError("Unexpected error creating the performance counter category named '{0}': {1}. Exception: {2}", _categoryName, e.Message, e);
            }
        }
    }
}
