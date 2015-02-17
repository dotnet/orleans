using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.ServiceModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OrleansUnitTestContainerLibrary
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
    public class UnitTestBusinessLogic : OrleansUnitTestContainerLibrary.IUnitTestBusinessLogic
    {


        private TestRunnerHelper.TestRunnerHelper helper = null;
        AppDomain domain = null;
        private int testCount;
        private int testsPassed = 0;
        private string logPath = null;
        private string startupDirectory = null;
        private IUnitTestCallBacks subscriber = null;
        public event Action<string, string> OnStartTest;
        public event Action<string, string> OnEndTest;
        public event Action<string, string> OnTestSuccess;
        public event Action<string, string, string> OnTestFailure;
        public event Action<MethodInfo[]> OnStartTestRun;
        public event Action<int, int, long> OnTestRunCompletion;
        public event Action<string> OnLoadTestUnitDll;
        public event Action<object> OnUnhandledException;
        public event Action OnCancel;


        public UnitTestBusinessLogic()
        {
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
        }

        void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (OnUnhandledException != null)
            {
                OnUnhandledException(e.ExceptionObject);
            }
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
                if (helper != null)
                {
                    helper.LogPath = value;
                }

            }
        }

        public string StartUpDirectory
        {
            get
            {
                return startupDirectory;
            }
            set
            {
                if (helper != null)
                {
                    helper.StartUpDirectory = value;
                }
                startupDirectory = value;
            }
        }

        public OrderBy RunOrder
        {
            get;
            set;
        }

        public MethodInfo[] LoadUnitTestDll(string assemblyFile)
        {
            //AppDomainSetup setup = new AppDomainSetup();
            //domain = AppDomain.CreateDomain("TestRunner");
            //domain.UnhandledException += new UnhandledExceptionEventHandler(domain_UnhandledException);
            //string assemblyName = System.Reflection.Assembly.GetAssembly(typeof(TestRunnerHelper.TestRunnerHelper)).FullName;
            //helper = (TestRunnerHelper.TestRunnerHelper)domain.CreateInstanceAndUnwrap(assemblyName, typeof(TestRunnerHelper.TestRunnerHelper).FullName);
            helper = new TestRunnerHelper.TestRunnerHelper();
            helper.OnStartTest += new Action<string, string>(helper_OnStartTest);
            helper.OnTestFailure += new Action<string, string, string>(helper_OnTestFailure);
            helper.OnTestRunCompletion += new Action<int, int, long>(helper_OnTestRunCompletion);
            helper.OnTestSuccess += new Action<string, string>(helper_OnTestSuccess);
            helper.OnCancel += new Action(helper_OnCancel);
            helper.OnEndTest += new Action<string, string>(helper_OnEndTest);
            helper.OnLoadTestUnitDll += new Action<string>(helper_OnLoadTestUnitDll);
            FireOnLoadTestUnitDll(assemblyFile);
            testCount = helper.GetTypes(assemblyFile);
            return LoadUnitTestDLLImpl(assemblyFile);
        }

        void domain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (OnUnhandledException != null)
            {
                OnUnhandledException(e.ExceptionObject);
            }
        }

        public MethodInfo[] LoadUnitTestDLLImpl(string assemblyFile)
        {
            Assembly assembly = Assembly.LoadFrom(assemblyFile);
            Type[] types = assembly.GetTypes();
            List<MethodInfo> unitTests = new List<MethodInfo>();
            foreach (Type unitTestClass in types)
            {
                if (unitTestClass.IsClass)
                {
                    TestClassAttribute[] attributes = (TestClassAttribute[])unitTestClass.GetCustomAttributes(typeof(TestClassAttribute), false);
                    if (attributes.Length > 0)
                    {
                        MethodInfo[] methodInfos = unitTestClass.GetMethods();
                        foreach (MethodInfo methodInfo in methodInfos)
                        {
                            if (methodInfo.GetCustomAttributes(typeof(TestMethodAttribute), false).Length > 0)
                            {
                                unitTests.Add(methodInfo);
                            }
                        }
                    }
                }
            }
            return unitTests.ToArray();
        }
        void helper_OnLoadTestUnitDll(string obj)
        {
            FireOnLoadTestUnitDll(obj);
        }

        void helper_OnEndTest(string arg1, string arg2)
        {
            FireOnEndTest(arg1, arg2);
        }

        void helper_OnCancel()
        {
            
        }

        void helper_OnTestSuccess(string arg1, string arg2)
        {
            FireOnTestSuccess(arg1, arg2);
        }

        void helper_OnTestRunCompletion(int arg1, int arg2, long arg3)
        {
            FireTestRunCompletion(arg1, arg2, arg3);
        }

        void helper_OnTestFailure(string arg1, string arg2, string arg3)
        {
            FireOnTestFailure(arg1, arg2, arg3);
        }

        void helper_OnStartTest(string arg1, string arg2)
        {
            FireOnStartTest(arg1, arg2);
        }

        public void RunUnitTests(MethodInfo[] unitTests, System.Threading.CancellationToken ct)
        {
            testsPassed = 0;
            FireOnStartTestRun(unitTests);
            System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
            sw.Start();

            unitTests = OrderUnitTests(RunOrder, unitTests);
            foreach (MethodInfo methodInfo in unitTests)
            {
                if (ct != null && ct.IsCancellationRequested)
                {
                    if (OnCancel != null)
                    {
                        OnCancel();
                    }
                    ct.ThrowIfCancellationRequested();
                    break;
                }

                RunUnitTest(methodInfo.DeclaringType.FullName, methodInfo.Name);

            }
            sw.Stop();
            FireTestRunCompletion(testsPassed, unitTests.Length, sw.ElapsedMilliseconds);
        }

        MethodInfo[] OrderUnitTests(OrderBy orderBy, MethodInfo[] unitTests)
        {
            switch (orderBy)
            {
                case OrderBy.TestClass:
                    return unitTests.OrderBy(new Func<MethodInfo, string>((methodInfo) =>
                    {
                        return methodInfo.DeclaringType.FullName;
                    })).ToArray();
                case  OrderBy.TestName:
                    return unitTests.OrderBy(new Func<MethodInfo, string>((methodInfo) =>
                    {
                        return methodInfo.Name;
                    })).ToArray();
                case  OrderBy.Random:
                    Random rand = new Random();
                    List<MethodInfo> newUnitTests = new List<MethodInfo>();
                    HashSet<int> indexes = new HashSet<int>();

                    while (newUnitTests.Count() < unitTests.Count())
                    {
                        int nextIndex = rand.Next(unitTests.Count());
                        if (!indexes.Contains(nextIndex))
                        {
                            indexes.Add(nextIndex);
                            newUnitTests.Add(unitTests[nextIndex]);
                        }
                    }
                    return newUnitTests.ToArray();
            }
            return unitTests;
        }
        private void RunUnitTest(string declaringType, string methodName)
        {
            helper.RunUnitTest(declaringType + methodName);
        }

        void FireOnTestSuccess(string methodName, string declaringType)
        {
            testsPassed++;
            try
            {
                if (OnTestSuccess != null)
                {
                    OnTestSuccess(methodName, declaringType);
                }
                if (subscriber != null)
                {
                    subscriber.OnTestSuccess(methodName, declaringType);
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

        void FireOnTestFailure(string methodName, string declaringType, string message)
        {
            try
            {
                if (OnTestFailure != null)
                {
                    OnTestFailure(methodName, declaringType, message);
                }
                if (subscriber != null)
                {
                    subscriber.OnTestFailure(methodName, declaringType, message);
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
                if (subscriber != null)
                {
                    subscriber.OnTestRunCompletion(totalPassed, totalFailed, testduration);
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine("Exception on Firing TestCompletion event-" + ex.ToString());
            }
        }
        void FireOnStartTestRun(MethodInfo[] unitTests)
        {
            try
            {
                if (OnStartTestRun != null)
                {
                    OnStartTestRun(unitTests);
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine("Exception on Firing OnStartTest event-" + ex.ToString());
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
        #region event subscription management
        public void Subscribe()
        {
            subscriber = OperationContext.Current.GetCallbackChannel<IUnitTestCallBacks>();
        }

        public void Unsubscribe()
        {
            subscriber = null;
        }
        #endregion
    }
}
