using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Reflection;
using System.ServiceModel;
using OrleansUnitTestContainerLibrary;

namespace OrleansUnitTestContainerConsoleApp
{
    class Program
    {
        static System.IO.TextWriter consoleWriter = System.Console.Out;
        static string logPath = null;
        static IUnitTestBusinessLogic businessLogic = new UnitTestBusinessLogic();
        static ProccessCommandLineArgements processArgs = new ProccessCommandLineArgements();
        static void Main(string[] args)
        {
            Console.SetOut(consoleWriter);
            processArgs.OrleansDirectory = System.Environment.CurrentDirectory;
            processArgs.ParseCommandLineArguments(args);
            if (args.Length > 0)
            {
                try
                {
                    businessLogic.OnStartTestRun += new Action<MethodInfo[]>(businessLogic_OnStartTestRun);
                    businessLogic.OnStartTest += new Action<string, string>(businessLogic_OnStartTest);
                    businessLogic.OnTestFailure += new Action<string, string, string>(businessLogic_OnTestFailure);
                    businessLogic.OnTestRunCompletion += new Action<int, int, long>(businessLogic_OnTestRunCompletion);
                    businessLogic.OnTestSuccess += new Action<string, string>(businessLogic_OnTestSuccess);
                    businessLogic.OnLoadTestUnitDll += new Action<string>(businessLogic_OnLoadTestUnitDll);
                    businessLogic.OnUnhandledException += new Action<object>(businessLogic_OnUnhandledException);
                    
                    List<MethodInfo> unitTests = new List<MethodInfo>(businessLogic.LoadUnitTestDll(args[0]));
                    if (processArgs.ScriptFile != null)
                    {
                        unitTests = GetUnitTestFromScriptFile(unitTests, processArgs.ScriptFile);
                    }
                    //businessLogic.SetLogPath(System.IO.Path.GetDirectoryName(args[0]));
                    businessLogic.RunOrder = OrderUnitTests(processArgs.TestMode);
                    businessLogic.RunUnitTests(unitTests.ToArray(), new System.Threading.CancellationToken());
                }
                catch (Exception ex)
                {
                    Console.SetOut(consoleWriter);
                    Console.WriteLine("Unexpected error encounter " + ex.ToString());
                }

                Console.WriteLine("Press any key to end unit test run...");
                Console.ReadKey();
            }
        }

        static void businessLogic_OnUnhandledException(object obj)
        {
            System.Diagnostics.Trace.WriteLine(string.Format("Unhandled exception encountered, application is terminating {0}", obj.ToString()));
            Console.SetOut(consoleWriter);
            Console.WriteLine("Unhandled exception encountered, see log for details");
        }

        static List<MethodInfo> GetUnitTestFromScriptFile(List<MethodInfo> unitTests, string scriptFile)
        {
            List<MethodInfo> newTests = new List<MethodInfo>();
            try
            {
                System.Xml.XmlDocument xmlDom = new System.Xml.XmlDocument();
                xmlDom.Load(System.IO.File.OpenText(scriptFile));
                XmlNodeList nodeList = xmlDom.SelectNodes("//TestClass/TestMethod");
                foreach (XmlElement node in nodeList)
                {
                    string methodName = node.Attributes["name"].Value;
                    string declaringType = node.ParentNode.Attributes["name"].Value;
                    MethodInfo mi = unitTests.Single<MethodInfo>((methodInfo) =>
                    {
                        if (methodInfo.Name == methodName && methodInfo.DeclaringType.FullName == declaringType)
                        {
                            return true;
                        }
                        else
                        {
                            return false;
                        }
                    });

                    if (mi != null)
                    {
                        newTests.Add(mi);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format("Unable to open script file {0} for reading, error message {1}", scriptFile, ex.Message));
            }
            return newTests;
        }

        static OrderBy OrderUnitTests(string testMode)
        {
            OrderBy orderBy = OrderBy.Default;
            if (testMode != null)
            {
                switch (testMode.ToUpper())
                {
                    case "TESTCLASS":
                        orderBy = OrderBy.TestClass;
                        break;
                    case "TESTNAME":
                        orderBy = OrderBy.TestName;
                        break;
                    case "RANDOM":
                        orderBy = OrderBy.Random;
                        break;
                    default:
                        Console.WriteLine("Warning: ORDERBY command line argument not recognized, running tests in default order");
                        break;
                }
            }
            return orderBy;
        }

        static void businessLogic_OnLoadTestUnitDll(string obj)
        {
        }

        static void  businessLogic_OnTestSuccess(string methodName, string declaringType)
        {
            System.IO.TextWriter writer = Console.Out;
            Console.SetOut(consoleWriter);
         	Console.WriteLine(" - passed");
            Console.SetOut(writer);
        }

        static void  businessLogic_OnTestRunCompletion(int arg1, int arg2, long duration)
        {
            Console.SetOut(consoleWriter);
            Console.WriteLine(string.Format("\nCompleted test run, {0}/{1} passed, Total test duration {2} ms", arg1, arg2, duration));
        }

        static void  businessLogic_OnTestFailure(string methodName, string declaringType, string errorMsg)
        {
            System.IO.TextWriter writer = Console.Out;
            Console.SetOut(consoleWriter);
            Console.WriteLine("- failed");
            Console.WriteLine(errorMsg);
            Console.SetOut(writer);
        }

        static void  businessLogic_OnStartTestRun(MethodInfo[] methodNames)
        {
            try
            {
                logPath = System.IO.Path.Combine(new string[] { System.Environment.CurrentDirectory, "Logs", DateTime.Now.ToLongDateString() });
                int runs = 2;
                while (System.IO.Directory.Exists(logPath))
                {
                    logPath = System.IO.Path.Combine(new string[] { System.Environment.CurrentDirectory, "Logs", DateTime.Now.ToLongDateString() });
                    logPath += string.Format("(Run {0})", runs++);
                }

                System.IO.Directory.CreateDirectory(logPath);
                businessLogic.LogPath = logPath;
                businessLogic.StartUpDirectory = processArgs.OrleansDirectory;
            }
            catch (Exception ex)
            {
            }

            System.IO.TextWriter writer = Console.Out;
            Console.SetOut(consoleWriter);
            Console.WriteLine(string.Format("Starting test run with {0} tests in {1} order \nlog file path is \"{2}\"\n Orleans Directory is \"{3}\"", methodNames.Length, businessLogic.RunOrder.ToString(), businessLogic.LogPath, businessLogic.StartUpDirectory));
            Console.SetOut(writer);
        }

        static void businessLogic_OnStartTest(string methodName, string declaringType)
        {
            System.IO.TextWriter writer = Console.Out;
            Console.SetOut(consoleWriter);
            Console.Write(string.Format("Executing test {0} defined in {1}", methodName, declaringType));
            Console.SetOut(consoleWriter);
        }

        public class ProccessCommandLineArgements
        {

            public bool ShowUI
            {
                get;
                set;
            }
            public string TestMode
            {
                get;
                set;
            }

            public string UnitTestDLLPath
            {
                get;
                set;
            }


            public string OrleansDirectory
            {
                get;
                set;
            }

            public string ScriptFile
            {
                get;
                set;
            }

            public void ParseCommandLineArguments(string[] args)
            {

                if (args.Length > 1)
                {
                    for (int i = 1; i < args.Length; i++)
                    {
                        string arg = args[i];
                        string[] nameValuePair = arg.Split(new char[] { '='});
                        if (nameValuePair.Length == 2)
                        {

                            switch (nameValuePair[0].ToUpper())
                            {
                                case "ORDERBY":
                                    this.TestMode = nameValuePair[1];
                                    break;
                                case "SCRIPT":
                                    this.ScriptFile = nameValuePair[1];
                                    break;
                                case "ORLEANSDIRECTORY":
                                    this.OrleansDirectory = nameValuePair[1];
                                    break;
                            }
                        }
                        else
                        {
                            throw new Exception(string.Format("Parsing Error, missing argument for name value pair {0}", nameValuePair));
                        }
                    }
                }
                else
                {
                    Console.WriteLine("Usage:OrleansUnitTestContainerConsoleApp.exe UnitTestDllPath [OrderBy=Random|TestClass|TestName] [Script=scriptPath] [OrleansDirectory=filepath]");
                }
            }
        }
    }
}
