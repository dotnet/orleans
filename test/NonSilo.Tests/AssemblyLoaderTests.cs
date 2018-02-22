using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.ApplicationParts;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost.Utils;
using Xunit;

namespace UnitTests
{
    public class AssemblyLoaderTests :IDisposable
    {
        const string ExpectedFileName = "OrleansProviders.dll";

        private readonly ILoggerFactory defaultLoggerFactory;

        private readonly ILogger logger;

        public AssemblyLoaderTests()
        {
            this.defaultLoggerFactory =
                TestingUtils.CreateDefaultLoggerFactory("AssemblyLoaderTests.log");
            this.logger = defaultLoggerFactory.CreateLogger<AssemblyLoaderTests>();
        }

        public void Dispose()
        {
            this.defaultLoggerFactory.Dispose();
        }

        [Fact, TestCategory("AssemblyLoader"), TestCategory("Functional")]
        public void AssemblyLoaderShouldDetectUnexpectedExceptionsDuringReflectionOnlyLoad()
        {
            logger.Info("AssemblyLoaderTests.AssemblyLoaderShouldDetectUnexpectedExceptionsDuringReflectionOnlyLoad");

            var exclusionList = NewExclusionList();
            var loader = NewAssemblyLoader(exclusionList);
            loader.SimulateReflectionOnlyLoadFailure = true;
            loader.RethrowDiscoveryExceptions = true;
            ExpectException(
                () =>
                    DiscoverAssemblies(loader, exclusionList));
        }

        [Fact, TestCategory("AssemblyLoader"), TestCategory("Functional")]
        public void AssemblyLoaderShouldDetectUnexpectedExceptionsDuringExcludeCriteria()
        {
            logger.Info("AssemblyLoaderTests.AssemblyLoaderShouldDetectUnexpectedExceptionsDuringExcludeCriteria");

            var exclusionList = NewExclusionList();
            var loader = NewAssemblyLoader(exclusionList);
            loader.SimulateLoadCriteriaFailure = true;
            loader.RethrowDiscoveryExceptions = true;
            ExpectException(
                () =>
                    DiscoverAssemblies(loader, exclusionList));
        }

        [Fact, TestCategory("AssemblyLoader"), TestCategory("Functional")]
        public void AssemblyLoaderShouldDetectUnexpectedExceptionsDuringLoadCriteria()
        {
            logger.Info("AssemblyLoaderTests.AssemblyLoaderShouldDetectUnexpectedExceptionsDuringLoadCriteria");

            var exclusionList = NewExclusionList();
            var loader = NewAssemblyLoader(exclusionList);
            loader.SimulateLoadCriteriaFailure = true;
            loader.RethrowDiscoveryExceptions = true;
            ExpectException(
                () =>
                    DiscoverAssemblies(loader, exclusionList));
        }

        [Fact, TestCategory("AssemblyLoader"), TestCategory("Functional")]
        public void AssemblyLoaderDiscoverExceptionsShouldNotBeRethrown()
        {
            logger.Info("AssemblyLoaderTests.AssemblyLoaderDiscoverExceptionsShouldNotBeRethrown");

            var exclusionList = NewExclusionList();

            var loader1 = NewAssemblyLoader(exclusionList);
            loader1.SimulateLoadCriteriaFailure = true;
            DiscoverAssemblies(loader1, exclusionList, validate: false);

            var loader2 = NewAssemblyLoader(exclusionList);
            loader2.SimulateExcludeCriteriaFailure = true;
            DiscoverAssemblies(loader2, exclusionList, validate: false);

            var loader3 = NewAssemblyLoader(exclusionList);
            loader3.SimulateReflectionOnlyLoadFailure = true;
            DiscoverAssemblies(loader3, exclusionList, validate: false);
        }

        private List<string> NewExclusionList()
        {
            return new List<string>(new[]
            {
                "Orleans.Core.dll",
                "OrleansRuntime.dll",
                "UnitTests.dll"
            });
        }

        private AssemblyLoader NewAssemblyLoader(List<string> exclusionList)
        {
            var directories =
                new Dictionary<string, SearchOption>
                    {
                        { Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath), SearchOption.AllDirectories }
                    };

            var excludeCriteria =
                new AssemblyLoaderPathNameCriterion[]
                    {
                        AssemblyLoaderCriteria.ExcludeResourceAssemblies,
                        AssemblyLoaderCriteria.ExcludeFileNames(exclusionList)
                    };
            var loadProvidersCriteria =
                new AssemblyLoaderReflectionCriterion[]
                    {
                        AssemblyLoaderCriteria.LoadTypesAssignableFrom(typeof(IProvider))
                    };

            return AssemblyLoader.NewAssemblyLoader(directories, excludeCriteria, loadProvidersCriteria, logger);
        }

        private void DiscoverAssemblies(AssemblyLoader loader, List<string> exclusionList, bool validate = true)
        {
            var result = loader.DiscoverAssemblies();

            var text = new StringBuilder();
            text.Append("\nFound assemblies:");
            foreach (var i in result)
                text.Append(String.Format("\n\t* {0}", i));
            logger.Info(text.ToString());

            if (validate)
            {
                var found = false;
                foreach (var i in result)
                {
                    var fileName = Path.GetFileName(i);
                    // we shouldn't have any blacklisted assemblies in the list.
                    Assert.False(exclusionList.Contains(fileName), "Assemblies on an exclusion list should be ignored.");     
                    if (fileName == ExpectedFileName)
                        found = true;
                }
                Assert.True(
                    found, 
                    String.Format(
                        "{0} should have been found by the assembly loader", 
                        ExpectedFileName));                
            }
        }

        private void ExpectException(Action action)
        {
            try
            {
                action();
            }
            catch (AggregateException e)
            {
                if (e.InnerExceptions.Count != 2 || 
                    e.InnerExceptions[0].Message != "Inner Exception #1" ||
                    e.InnerExceptions[1].Message != "Inner Exception #2")
                {
                    throw;
                }
            }
        }
        
    }
}
