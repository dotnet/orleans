using System;
using System.Reflection;
using System.ServiceModel;

namespace OrleansUnitTestContainerLibrary
{
    public enum OrderBy { Random, TestName, TestClass, Default }
    public interface IUnitTestBusinessLogic
    {
        event Action<string, string, string> OnTestFailure;
        event Action<string, string> OnStartTest;
        event Action<string, string> OnEndTest;
        event Action<string, string> OnTestSuccess;
        event Action<MethodInfo[]> OnStartTestRun;
        event Action<int, int, long> OnTestRunCompletion;
        event Action<string> OnLoadTestUnitDll;
        event Action<object> OnUnhandledException;
        event Action OnCancel;

        string LogPath { get; set; }
        string StartUpDirectory { get; set; }

        MethodInfo[] LoadUnitTestDll(string assemblyPath);

        void RunUnitTests(MethodInfo[] unitTests, System.Threading.CancellationToken ct);

        OrderBy RunOrder
        {
            get;
            set;
        }

        void Subscribe();

        void Unsubscribe();
    }

    public interface IUnitTestCallBacks
    {
        [OperationContract(IsOneWay = true)]
        void OnTestFailure(string methodName, string declaringType, string message);

        [OperationContract(IsOneWay = true)]
        void OnTestSuccess(string methodName, string declaringType);

        [OperationContract(IsOneWay = true)]
        void OnStartTestRun(string[] methodNames);

        [OperationContract(IsOneWay = true)]
        void OnTestRunCompletion(int passed, int failed, long testDuration);
    }
}
