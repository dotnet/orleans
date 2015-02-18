using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestRunnerHelper 
{
    public class UnitTestContext : TestContext
    {
        private Dictionary<string, object> properties = new Dictionary<string, object>();
        public override void AddResultFile(string fileName)
        {
            throw new NotImplementedException();
        }

        public override void BeginTimer(string timerName)
        {
            throw new NotImplementedException();
        }

        public override System.Data.Common.DbConnection DataConnection
        {
            get { throw new NotImplementedException(); }
        }

        public override System.Data.DataRow DataRow
        {
            get { throw new NotImplementedException(); }
        }

        public override void EndTimer(string timerName)
        {
            throw new NotImplementedException();
        }

        public override System.Collections.IDictionary Properties
        {
            get { return properties; }
        }

        public override void WriteLine(string format, params object[] args)
        {
            throw new NotImplementedException();
        }
    }
    
    [Serializable]
    public class TestRunnerHelper : System.MarshalByRefObject
    {
        private Dictionary<string, object> runningInstances = new Dictionary<string, object>();
        private Dictionary<string, MethodInfo> testInitializeDictionary = new Dictionary<string, MethodInfo>();
        private Dictionary<string, MethodInfo> testCleanupDictionary = new Dictionary<string, MethodInfo>();
        private Dictionary<string, MethodInfo> classInitializeDictionary = new Dictionary<string, MethodInfo>();
        private Dictionary<string, MethodInfo> classCleanupDictionary = new Dictionary<string, MethodInfo>();
        private Dictionary<string, Type> expectedExceptionDictionary = new Dictionary<string, Type>();
        private Dictionary<string, MethodInfo> unitTestDictionary = new Dictionary<string, MethodInfo>();

        public event Action<string, string> OnStartTest;
        public event Action<string, string> OnEndTest;
        public event Action<string, string> OnTestSuccess;
        public event Action<string, string, string> OnTestFailure;
        public event Action<int, int, long> OnTestRunCompletion;
        public event Action<string> OnLoadTestUnitDll;
        public event Action<string> OnUnhandledException;
        public event Action OnCancel;

        private string assemblyFile;
        private string assemblyPath;
        Assembly unitTestAssembly = null;

        private string logPath = null;
        private PropertyInfo TestContextProperty = null;
        public TestRunnerHelper()
        {
            //AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
            
        }

        public string LogPath
        {

            get
            {
                if (logPath == null)
                {
                    logPath = System.Environment.CurrentDirectory;
                }
                return logPath;
            }
            set
            {
                Console.WriteLine("SetLogPath called");
                logPath = value;
            }
        }

        public string StartUpDirectory
        {
            get;
            set;
        }

        void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            FireUnhandledException(e.ExceptionObject.ToString());
            //System.Threading.Thread.CurrentThread.Abort();
        }

        public int GetTypes(string assemblyFile)
        {
            this.assemblyFile = assemblyFile;
            this.assemblyPath = System.IO.Path.GetDirectoryName(assemblyFile);
            this.unitTestAssembly = Assembly.LoadFrom(assemblyFile);
            Type[] types = unitTestAssembly.GetTypes();
            foreach (Type unitTestClass in types)
            {
                if (unitTestClass.IsClass)
                {
                    if (unitTestClass.Name == "UnitTestBase")
                    {
                        TestContextProperty = unitTestClass.GetProperty("context");
                    }

                    TestClassAttribute[] attributes = (TestClassAttribute[])unitTestClass.GetCustomAttributes(typeof(TestClassAttribute), false);
                    if (attributes.Length > 0)
                    {
                        MethodInfo[] methodInfos = unitTestClass.GetMethods();
                        foreach (MethodInfo methodInfo in methodInfos)
                        {
                            if (methodInfo.GetCustomAttributes(typeof(ClassInitializeAttribute), false).Length > 0)
                            {
                                classInitializeDictionary.Add(unitTestClass.FullName, methodInfo);
                            }
                            else if (methodInfo.GetCustomAttributes(typeof(ClassCleanupAttribute), false).Length > 0)
                            {
                                classCleanupDictionary.Add(unitTestClass.FullName, methodInfo);
                            }
                            else if (methodInfo.GetCustomAttributes(typeof(TestInitializeAttribute), false).Length > 0)
                            {
                                testInitializeDictionary.Add(unitTestClass.FullName, methodInfo);
                            }
                            else if (methodInfo.GetCustomAttributes(typeof(TestCleanupAttribute), false).Length > 0)
                            {
                                testCleanupDictionary.Add(unitTestClass.FullName, methodInfo);
                            }
                            else if (methodInfo.GetCustomAttributes(typeof(TestMethodAttribute), false).Length > 0)
                            {
                                unitTestDictionary.Add(methodInfo.DeclaringType.FullName + methodInfo.Name, methodInfo);
                                if (methodInfo.GetCustomAttributes(typeof(ExpectedExceptionAttribute), false).Length > 0)
                                {
                                    ExpectedExceptionAttribute customAttrib = (ExpectedExceptionAttribute)methodInfo.GetCustomAttributes(typeof(ExpectedExceptionAttribute), false)[0];
                                    expectedExceptionDictionary.Add(methodInfo.Name, customAttrib.ExceptionType);
                                }
                            }
                        }
                    }
                }
            }

            
            return unitTestDictionary.Count();
        }

        public void RunUnitTest(string key)
        {
            object classInstance = null;
            MethodInfo methodInfo = unitTestDictionary[key];

            #region Initialize test context property
            UnitTestContext context = new UnitTestContext();
            if (TestContextProperty != null)
            {
                context.Properties["LogPath"] = System.IO.Path.Combine(LogPath, methodInfo.DeclaringType.FullName, methodInfo.Name);
                context.Properties["InputDirectory"] = StartUpDirectory;
                System.Environment.CurrentDirectory = StartUpDirectory;
                TestContextProperty.SetValue(null, context, null);
            }
            #endregion
            if (methodInfo != null)
            {
                try
                {
                    InitializeLogPath(methodInfo.Name, methodInfo.DeclaringType.FullName);
                    if (classInstance != null && classInstance.GetType().FullName != methodInfo.DeclaringType.FullName)
                    {
                        if (classCleanupDictionary.ContainsKey(classInstance.GetType().FullName))
                        {
                            classCleanupDictionary[classInstance.GetType().FullName].Invoke(classInstance, new object[] { });
                        }
                    }

                    FireOnStartTest(methodInfo.Name, methodInfo.DeclaringType.FullName);
                    classInstance = unitTestAssembly.CreateInstance(methodInfo.DeclaringType.FullName);
                    if (runningInstances.ContainsKey(methodInfo.DeclaringType.FullName))
                    {
                        //classInstance = runningInstances[methodInfo.DeclaringType.FullName];
                    }
                    else // create new instance
                    {
                        //classInstance = unitTestDLL.CreateInstance(methodInfo.DeclaringType.FullName);
                        if (classInitializeDictionary.ContainsKey(methodInfo.DeclaringType.FullName))
                        {
                            classInitializeDictionary[methodInfo.DeclaringType.FullName].Invoke(classInstance, new object[] { context });
                        }
                        runningInstances.Add(methodInfo.DeclaringType.FullName, classInstance);

                    }

                    #region test intialize
                    if (testInitializeDictionary.ContainsKey(methodInfo.DeclaringType.FullName))
                    {
                        testInitializeDictionary[methodInfo.DeclaringType.FullName].Invoke(classInstance, null);
                    }
                    #endregion
                    methodInfo.Invoke(classInstance, new object[] { });
                    FireOnTestSuccess(methodInfo.Name, methodInfo.DeclaringType.FullName);
                    System.Diagnostics.Trace.WriteLine(string.Format("Test has succeded."));
                }
                catch (Exception ex)
                {
                    if (expectedExceptionDictionary.ContainsKey(methodInfo.Name) && ex.InnerException != null && expectedExceptionDictionary[methodInfo.Name] == ex.InnerException.GetType())
                    {
                        // exception is expected therefore we have success
                        FireOnTestSuccess(methodInfo.Name, methodInfo.DeclaringType.FullName);
                    }
                    else
                    {
                        FireOnTestFailure(methodInfo.Name, methodInfo.DeclaringType.FullName, ex.InnerException != null ? ex.InnerException : ex);
                    }
                    System.Diagnostics.Trace.WriteLine(string.Format("Test has thrown exception: {0}", ex));
                }
                finally
                {
                    #region cleanup
                    if (testCleanupDictionary.ContainsKey(methodInfo.DeclaringType.FullName))
                    {
                        if (classInstance != null)
                        {
                            testCleanupDictionary[methodInfo.DeclaringType.FullName].Invoke(classInstance, new object[] { });
                        }
                    }
                    FireOnEndTest(methodInfo.Name, methodInfo.DeclaringType.FullName);
                    #endregion
                }
            }
        }

        void InitializeLogPath(string methodName, string declaringType)
        {
            try
            {
                TextWriterTraceListener textListener = null;
                string path = System.IO.Path.Combine(LogPath, declaringType, methodName);
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                }


                foreach (TraceListener listener in Trace.Listeners)
                {
                    if (listener is TextWriterTraceListener)
                    {
                        listener.Flush();
                        listener.Close();
                        textListener = listener as TextWriterTraceListener;
                        break;
                    }
                }

                if (textListener == null)
                {
                    textListener = new System.Diagnostics.TextWriterTraceListener();
                    Trace.Listeners.Add(textListener);
                }

                System.IO.StreamWriter sw = null;
                string fileName = System.IO.Path.Combine(path, methodName + ".log");
                try
                {
                    sw = new System.IO.StreamWriter(fileName, true);
                }
                catch (Exception)
                {
                    path = System.IO.Path.Combine(new string[] { path, Guid.NewGuid().ToString() + fileName });
                    sw = new System.IO.StreamWriter(path, true);
                }
                finally
                {
                    textListener.Writer = sw;
                    System.Console.SetOut(textListener.Writer);
                }
            }
            catch (Exception)
            {
            }
        }

        void FireOnTestSuccess(string methodName, string declaringType)
        {
            //testsPassed++;
            try
            {
                if (OnTestSuccess != null)
                {
                    OnTestSuccess(methodName, declaringType);
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine("Exception on Firing OnTestSuccess event-" + ex.ToString());
            }
        }

        void FireOnStartTest(string methodName, string declaringType)
        {
            try
            {
                if (OnStartTest != null)
                {
                    OnStartTest(methodName, declaringType);
                }
            }
            catch (Exception ex)
            {
            }
        }

        void FireOnEndTest(string methodName, string declaringType)
        {
            try
            {
                if (OnEndTest != null)
                {
                    OnEndTest(methodName, declaringType);
                }
            }
            catch (Exception ex)
            {
            }
        }

        void FireOnTestFailure(string methodName, string declaringType, Exception ex)
        {
            try
            {
                if (OnTestFailure != null)
                {
                    OnTestFailure(methodName, declaringType, ex.Message);
                }
            }
            catch (Exception e)
            {
                //Console.WriteLine("Exception on Firing OnTestFailure event-" + e.ToString());
            }
        }

        void FireTestRunCompletion(int totalPassed, int totalFailed, long testduration)
        {
            try
            {
                if (OnTestRunCompletion != null)
                {
                    OnTestRunCompletion(totalPassed, totalFailed, testduration);
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine("Exception on Firing TestCompletion event-" + ex.ToString());
            }
        }

        void FireOnLoadTestUnitDll(string path)
        {
            try
            {
                if (OnLoadTestUnitDll != null)
                {
                    OnLoadTestUnitDll(path);
                }
            }
            catch
            {
            }
        }

        void FireUnhandledException(string msg)
        {
            try
            {
                if (OnUnhandledException != null)
                {
                    OnUnhandledException(msg);
                }
            }
            catch
            {
            }
        }
    }


}
