using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestFramework;

// ReSharper disable UnusedVariable

namespace LoadTest
{
    public class LoadTestBase
    {
        public TestContext TestContext { get; set; }

        protected readonly TestConfig testConfig = new TestConfig();
        public void BasePrologue()
        {
            try
            {
                testConfig.TestContext = TestContext;
                Log.Init(testConfig);
                Log.WriteLine(SEV.STATUS, "Config.Init", "User:{0}@{1} on machine {2}", Environment.UserName, Environment.UserDomainName, Environment.MachineName);
                Log.WriteLine(SEV.STATUS, "Config.Init", "Location:{0}", typeof(LoadTest).Assembly.Location);
                TaskHelper.Init();
            }
            catch (Exception)
            {
            }
        }

        public void BaseEpilogue()
        {
            Log.SendLogs("Load Test Results available for " + TestContext.TestName,
                Log.GetResultsHtml(TestContext.TestName, TestContext.CurrentTestOutcome));
        }

        protected void TestLoadScenario(
            string deploymentConfig, 
            string clusterName,
            string metricCollector, 
            ClientOptions deploymentOptions,
            string clientGrammar = "ClientLog", 
            string serverGrammar = "ServerLog",
            SiloOptions siloOptions = null)
        {
            DeploymentManager deployManager = null;
            try
            {
                DumpTestOptions(deploymentConfig, metricCollector, deploymentOptions);

                //--- STEP 0: Clean up ---
                DeploymentConfig dc = testConfig.GetDeploymentConfig(deploymentConfig, clusterName);
                dc.SelectClient(deploymentOptions.ClientAppName);
                dc.SiloOptions = siloOptions;
                deployManager = new DeploymentManager(dc);
                deployManager.CleanUp();
                ParserGrammar svrGrmr = testConfig.GetGrammar(serverGrammar);
                ParserGrammar grammar = testConfig.GetGrammar(clientGrammar); 
                MetricCollector collector = testConfig.GetMetricCollector(metricCollector);

                //--- STEP 1- 4: Start test environment
                deployManager.StartTestEnvironment(deploymentOptions, svrGrmr, grammar, collector);
                collector.BeginAnalysis();


                //--- STEP 5: Start Background tasks ---
                Task[] tasks = new Task[] 
                {
                    // Task A : In a while loop keep on reading, aggregating and asserting metrics collected.
                    Task.Factory.StartNew(()=> 
                    {
                        collector.ProcessInBackground();
                    }),
                    // Task B : Keep watch on server processes.
                    Task.Factory.StartNew(()=> 
                    {
                        deployManager.BabysitSilos();
                    }),
                    // Task C : Keep watch on client processes.
                    Task.Factory.StartNew(()=> 
                    {
                        deployManager.BabysitClients();
                    }),
                    // Task D : Wait until one of the client finishes successfully and then stop metric collection.
                    Task.Factory.StartNew(()=> 
                    {
                        if(QuickParser.DEBUG_ONLY_NO_WAITING)
                        {
                            TimeSpan testTime = TimeSpan.FromMinutes(6);
                            Thread.Sleep(testTime);
                            Log.WriteLine(SEV.INFO, "TestResults", "Test is about to finish after {0} since QuickParser.DEBUG_ONLY_NO_WAITING is true.", testTime);
                        }
                        //wait for ANY one client to finish.
                        QuickParser.WaitForStateAny(deployManager.Logs, "Finished");
                        // stop aggregating and asserting now onwards
                        collector.EndAnalysis();
                    }),
                };

                //--- STEP 6: Wait until background processing is completed successfully or exception is thrown ---
                int indx = Task.WaitAny(tasks);
                if (tasks[indx].IsFaulted)
                {
                    deployManager.TestFinished(); 
                    collector.EndAnalysis();
                    TaskHelper.LogTaskFailuresAndThrow(tasks);
                }

                //--- STEP 7: Wait for all client to finish ---
                if (!collector.ExitEarly)
                {
                    QuickParser.WaitForStateAll(deployManager.Logs, "Finished");
                }
                deployManager.TestFinished();
            }
            catch (Exception ex)
            {
                Log.WriteLine(SEV.ERROR, "TestResults", "Test Failed with exception:{0}", ex);
                throw new AggregateException(ex);
            }
            finally
            {
                //--- STEP 8: Cleanup and save logs ---
                if (QuickParser.WAIT_BEFORE_KILLING_SILOS > TimeSpan.Zero)
                {
                    Thread.Sleep(QuickParser.WAIT_BEFORE_KILLING_SILOS);
                }
                if (deployManager != null) deployManager.CleanUp();
                if (deployManager != null) deployManager.SaveLogs();
                TaskHelper.CheckUnobservedExceptions();
            }
        }

        protected void TestFailoverScenario(
           string deploymentConfig,
           string clusterName,
           string metricCollector,
           ClientOptions deploymentOptions,
           bool restart, 
           int serversToRestart = 5,
           string clientGrammar = "ClientLog",
           string serverGrammar = "ServerLog",
           bool analyze = false)
        {
            DumpTestOptions(deploymentConfig, metricCollector, deploymentOptions);

            //--- STEP 0: Clean up ---
            var dc = testConfig.GetDeploymentConfig(deploymentConfig, clusterName);
            dc.SelectClient(deploymentOptions.ClientAppName);
            DeploymentManager deployManager = new DeploymentManager(dc);
            deployManager.CleanUp();

            try
            {
                ParserGrammar svrGrmr = testConfig.GetGrammar(serverGrammar);
                ParserGrammar grammar = testConfig.GetGrammar(clientGrammar);
                MetricCollector collector = testConfig.GetMetricCollector(metricCollector);
                
                //--- STEP 1- 4: Start test environment
                deployManager.StartTestEnvironment(deploymentOptions, svrGrmr, grammar, collector);
                string TIME_TO_RECOVER_HEADER = "Time To Recover";
                Log.TestResults.AddHeaders(new string[] { TIME_TO_RECOVER_HEADER });

                //--- STEP 5: Start Background tasks ---
                Task[] babySittingTasks = new Task[] 
                {
                    // Task B : Keep watch on server processes.
                    Task.Factory.StartNew(()=> 
                    {
                        deployManager.BabysitSilos();
                    }),
                    // Task C : Keep watch on client processes.
                    Task.Factory.StartNew(()=> 
                    {
                        deployManager.BabysitClients();
                    }),
                };

                int silosToStop = deployManager.Silos.Count - deploymentOptions.ClientCount * deploymentOptions.ServersPerClient;
                for (int i = 0; i < silosToStop; i += serversToRestart)
                {
                    int batch = i / serversToRestart;
                    Log.WriteLine(SEV.STATUS, "TEST", "============================= Batch {0} =============================", batch);
                    for (int j = 0; j < serversToRestart; j++)
                    {
                        SiloHandle silo = deployManager.Silos[i + j];
                        Log.WriteLine(SEV.STATUS, "TEST", "Action #{0}, {1} SiloName = {2}", i + j, (restart ? "Restart" : "Stop"), silo.Name);
                        if (restart)
                        {
                            deployManager.RestartSilo(silo);
                        }
                        else
                        {
                            deployManager.StopSilo(silo);
                        }
                    }

                    
                    DateTime t0 = DateTime.UtcNow;
                    foreach (QuickParser client in deployManager.Logs)
                    {
                        client.ForceState("Unstable");
                        client.TransitionCallbacks.Add("Stable", (qp, first, last) =>
                            {
                                DateTime t1 = t0;
                                int indx = first.LastIndexOf("Timestamp:");
                                indx = indx + "Timestamp:".Length;
                                string time = first.Substring(indx);
                                DateTime t2 = DateTime.Parse(time);
                                TimeSpan diff = t2 - t1;
                                Log.WriteLine(SEV.STATUS, "Metric.Result", "Time To Recover for batch {0} = {1}", batch, diff);
                                Record record = new Record();
                                record[TIME_TO_RECOVER_HEADER] = diff;
                                Log.TestResults.AddRecord(batch, record);
                            });
                    }
                    // default sleep time between status check is 100 milliseconds, so wait for 10 minutes 100*100 mil
                    int maxStatusChecks = 60 * 100;
                    Thread.Sleep(TimeSpan.FromSeconds(15));
                    deployManager.DumpAzureTablePartition();
                    QuickParser.WaitForStateAll(deployManager.Logs, "Stable", checkVisited: false, max: maxStatusChecks);
                    // Readjust the scale factor
                    if (!restart)
                    {
                        int newServerCount = deployManager.Silos.Count - deployManager.Silos.Where(silo => !silo.IsRunning).Count();
                        collector.ChangeVariable("ServerCount", newServerCount);
                        double newScaleFactor = Math.Min(
                            deploymentOptions.ClientCount * deploymentOptions.ServersPerClient * 1000,
                            newServerCount * 2500);
                        collector.ChangeVariable("ScaleFactor", newScaleFactor);
                        collector.ChangeVariable("ScaleFactorPerClient", newScaleFactor / deploymentOptions.ClientCount);
                    }
                    if (analyze)
                    {
                        //MetricCollector.EarlyResultCount = 10;
                        collector.ExitEarly = true;
                        collector.BeginAnalysis();
                        collector.ProcessInBackground();
                        collector.EndAnalysis();
                    }
                    foreach (QuickParser client in deployManager.Logs)
                    {
                       client.TransitionCallbacks.Remove("Stable");
                    }

                    // Actually we need to check that no silo/client crashed after every iteration.
                    foreach (var babySittingTask in babySittingTasks)
                    {
                        if (babySittingTask.IsFaulted)
                        {
                            deployManager.TestFinished();
                            collector.EndAnalysis();
                            TaskHelper.LogTaskFailuresAndThrow(babySittingTasks);
                        }
                    }
                }
                
                ////--- STEP 7: Wait for all client to finish ---
                deployManager.TestFinished();
            }
            catch (Exception ex)
            {
                
                Log.WriteLine(SEV.ERROR, "TestResults", "Test Failed with exception:{0}", ex);
                Log.WriteLine(SEV.STATUS, "TEST", "----------DUMPING AZURE TABLE---------");
                deployManager.DumpAzureTablePartition();
                Log.WriteLine(SEV.STATUS, "TEST", "******************");
                throw new AggregateException(ex);
            }
            finally
            {
                //--- STEP 8: Cleanup and save logs ---
                deployManager.CleanUp();
                deployManager.SaveLogs();
                TaskHelper.CheckUnobservedExceptions();
            }
        }


        protected void TestOldLogData(
            string deploymentConfig,
            string clusterName,
            string metricCollector,
            ClientOptions deploymentOptions,
            string directoryName,
            string filePrefix,
            string clientGrammar = "ClientLog",
            string serverGrammar = "ServerLog")
        {
            DumpTestOptions(deploymentConfig, metricCollector, deploymentOptions);

            //--- STEP 0: Clean up ---
            var dc = testConfig.GetDeploymentConfig(deploymentConfig, clusterName);
            dc.SelectClient(deploymentOptions.ClientAppName);
            DeploymentManager deployManager = new DeploymentManager(dc, cleanup: false);

            try
            {
                ParserGrammar svrGrmr = testConfig.GetGrammar(serverGrammar);
                ParserGrammar grammar = testConfig.GetGrammar(clientGrammar);
                MetricCollector collector = testConfig.GetMetricCollector(metricCollector);

                var logFiles = deployManager.FindLogFiles(filePrefix, directoryName, new DateTime());
                List<QuickParser> parsers = new List<QuickParser>();
                foreach (string logFile in logFiles)
                {
                    collector.AddSender(logFile);
                    QuickParser logParser = new QuickParser(grammar);
                    parsers.Add(logParser);
                    logParser.MetricCollector = collector;
                    logParser.BeginAnalysis(logFile, new FileObserver(logFile, 1000, 1, 1)); // read at least once, at most once
                }
                deployManager.SetRuntimeVariables(deploymentOptions, collector);
                collector.BeginAnalysis();
                QuickParser.WaitForStateAll(parsers, "Stable");
                collector.ProcessInBackground();
                QuickParser.WaitForStateAny(parsers, "Finished");
                // stop aggregating and asserting now onwards
                collector.EndAnalysis();

                //--- STEP 7: Wait for all client to finish ---
                if (!collector.ExitEarly)
                {
                    QuickParser.WaitForStateAll(parsers, "Finished");
                }
                foreach (var parser in parsers) parser.EndAnalysis();
            }
            catch (Exception ex)
            {
                Log.WriteLine(SEV.ERROR, "TestResults", "Test Failed with exception:{0}", ex);
                throw new AggregateException(ex);
            }
        }
   
        protected void DumpTestOptions(string deploymentConfig, string metricCollector, ClientOptions deploymentOptions)
        {
            Log.WriteLine(SEV.INFO, "Config.Init", "Deployment Config:{0}", deploymentConfig);
            Log.WriteLine(SEV.INFO, "Config.Init", "Metric Config:{0}", metricCollector);
            Log.WriteLine(SEV.INFO, "Config.Init", "ServerCount:{0}", deploymentOptions.ServerCount);
            Log.WriteLine(SEV.INFO, "Config.Init", "ClientCount:{0}", deploymentOptions.ClientCount);
            Log.WriteLine(SEV.INFO, "Config.Init", "ServersPerClient:{0}", deploymentOptions.ServersPerClient);
            Log.WriteLine(SEV.INFO, "Config.Init", "Using TestConfigFile:{0}", testConfig.TestConfigFile);
        }
    }
}
// ReSharper restore UnusedVariable
