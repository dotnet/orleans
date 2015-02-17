using System;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Xml;
using System.IO;
using System.Globalization;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTestGrains;

namespace UnitTests.BenchmarkTests
{
    /// <summary>
    /// Summary description for BenchmarkTests
    /// </summary>
    [TestClass]
    public class MicroBenchmarkTests : UnitTestBase
    {

        // To fix remoting issue:  Want to run the normal 'class cleanup' thing after every test
        // Use TestCleanup to run code after each test has run
        [TestCleanup]        
        public void MyTestCleanup()
        {
            ResetDefaultRuntimes();
        }
        // Use TestInitialize to run code before running each test 
        [TestInitialize]
        public void MyTestInitialize()
        {
        }
        
        
        private static int timeout = Debugger.IsAttached ? 300 * 1000 : 300 * 1000;

        //private static IBenchmarkGrain benchmarkGrainOne;
        //private static IBenchmarkGrain benchmarkGrainTwo;

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        private static void InitializeBenchmarkGrains(bool sameSilo)
        {
            //benchmarkGrainOne = BenchmarkGrainFactory.CreateGrain(
            //    Name: Guid.NewGuid().ToString(),
            //    Strategies: new[] { GrainStrategy.PartitionPlacement(0, 2) });

            //benchmarkGrainTwo = BenchmarkGrainFactory.CreateGrain(
            //    Name: Guid.NewGuid().ToString(),
            //    Strategies: new[] { GrainStrategy.PartitionPlacement(sameSilo ? 0 : 1, 2) });

            //benchmarkGrainOne.Wait();
            //benchmarkGrainTwo.Wait();
        }

        protected static void LogMetric(string metricName, object metricValue, string categories = null)
        {
            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                if (File.Exists("performanceLog.xml"))
                {
                    xmlDoc.Load("performanceLog.xml");
                }
                else
                {
                    XmlElement root = xmlDoc.CreateElement("PerformanceLog");
                    xmlDoc.AppendChild(root);
                    XmlAttribute attrib = xmlDoc.CreateAttribute("DateTime");
                    attrib.Value = DateTime.UtcNow.ToString(CultureInfo.InvariantCulture);
                    root.Attributes.Append(attrib);
                    string machineName = System.Environment.MachineName;
                    attrib = xmlDoc.CreateAttribute("MachineName");
                    attrib.Value = machineName;
                    root.Attributes.Append(attrib);
                    attrib = xmlDoc.CreateAttribute("BuildId");
                    attrib.Value = "BUILD_ID";
                    root.Attributes.Append(attrib);
                }

                WriteRecord(xmlDoc, metricName, metricValue, categories);
                xmlDoc.Save("performanceLog.xml");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unable to log metric" + ex);
            }
        }

        private static void WriteRecord(XmlDocument doc, string metricName, object metricValue, string categories)
        {
            StackTrace stackTrace = new StackTrace();
            StackFrame stackFrame = stackTrace.GetFrame(2);
            MethodBase methodBase = stackFrame.GetMethod();
            string methodName = methodBase.Name;
            Type methodType = methodBase.ReflectedType;

            XmlNode root = doc.SelectSingleNode("/PerformanceLog");
            XmlElement newElement = doc.CreateElement("LogEntry");
            root.AppendChild(newElement);
            XmlAttribute attrib = doc.CreateAttribute("ClassName");
            attrib.Value = methodType.Name;
            newElement.Attributes.Append(attrib);
            attrib = doc.CreateAttribute("MethodName");
            attrib.Value = methodName;
            newElement.Attributes.Append(attrib);
            attrib = doc.CreateAttribute("MetricName");
            attrib.Value = metricName;
            newElement.Attributes.Append(attrib);
            attrib = doc.CreateAttribute("MetricValue");
            attrib.Value = metricValue.ToString();
            newElement.Attributes.Append(attrib);

            if (categories != null)
            {
                foreach (string category in categories.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    XmlElement element = doc.CreateElement("Category");
                    element.InnerText = category.Trim();
                    newElement.AppendChild(element);
                }
            }
        }


        private static double RunBenchmarkReflected(bool sameSilo, int numIterations, int numDropIterations,
            string testName, string functionName, object[] data, string categories = "", double timeThreshold = 0.0)
        {
            InitializeBenchmarkGrains(sameSilo);
            Stopwatch s = new Stopwatch();

            for (int i = 0; i < numDropIterations + numIterations; i++)
            {
                if (i == numDropIterations)
                    s.Start();

                System.Reflection.MethodInfo method = typeof(IBenchmarkGrain).GetMethod(functionName);
            }
            s.Stop();

            double timeActual = s.Elapsed.TotalMilliseconds / numIterations;

            if (timeThreshold > 0.0)
                if (timeActual > timeThreshold)
                    throw new Exception(string.Format("{2} test was over the threshold: Actual {0:F3}ms, expected {1:F3}ms",
                        timeActual, timeThreshold, testName));

            return timeActual;
        }

        private static double RunBenchmarkIntergrainReflected(bool sameSilo, int numIterations, int numDropIterations,
            string testName, string functionName, object[] data, string categories = "", double timeThreshold = 0.0)
        {
            InitializeBenchmarkGrains(sameSilo);

            double timeActual = 0f;//benchmarkGrainOne.IntergrainBenchmarkReflected(benchmarkGrainTwo, numIterations, numDropIterations, functionName, data).Result;

            if (timeThreshold > 0.0)
                if (timeActual > timeThreshold)
                    throw new Exception(string.Format("{2} test was over the threshold: Actual {0:F3}ms, expected {1:F3}ms",
                        timeActual, timeThreshold, testName));

            return timeActual;
        }

        #region Client->Grain Read
        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Single_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Single_Byte",
                    functionName: "ReadByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, single, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Single_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Single_Float",
                    functionName: "ReadFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, single, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Single_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Single_String",
                    functionName: "ReadString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, single, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Single_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Single_Class",
                    functionName: "ReadClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, single, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Single_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Single_Struct",
                    functionName: "ReadStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, single, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Array_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Array_Byte",
                    functionName: "ReadArrayByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, array, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Array_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Array_Float",
                    functionName: "ReadArrayFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, array, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Array_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Array_String",
                    functionName: "ReadArrayString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, array, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Array_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Array_Class",
                    functionName: "ReadArrayClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, array, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Array_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Array_Struct",
                    functionName: "ReadArrayStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, array, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Dictionary_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Dictionary_Byte",
                    functionName: "ReadDictionaryByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, dictionary, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Dictionary_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Dictionary_Float",
                    functionName: "ReadDictionaryFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, dictionary, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Dictionary_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Dictionary_String",
                    functionName: "ReadDictionaryString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, dictionary, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Dictionary_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Dictionary_Class",
                    functionName: "ReadDictionaryClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, dictionary, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_Dictionary_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_Dictionary_Struct",
                    functionName: "ReadDictionaryStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, dictionary, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_List_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_List_Byte",
                    functionName: "ReadListByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, list, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_List_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_List_Float",
                    functionName: "ReadListFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, list, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_List_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_List_String",
                    functionName: "ReadListString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, list, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_List_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_List_Class",
                    functionName: "ReadListClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, list, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_R_List_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_R_List_Struct",
                    functionName: "ReadListStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, read, list, struct");
        }

        #endregion
        #region Client->Grain Write

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Single_Byte()
        {
            byte data = (byte)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Single_Byte",
                    functionName: "WriteByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, single, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Single_Float()
        {
            float data = (float)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Single_Float",
                    functionName: "WriteFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, single, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Single_String()
        {
            string data = (string)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Single_String",
                    functionName: "WriteString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, single, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Single_Class()
        {
            BenchmarkGrainDataClass data = (BenchmarkGrainDataClass)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Single_Class",
                    functionName: "WriteClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, single, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Single_Struct()
        {
            BenchmarkGrainDataStruct data = (BenchmarkGrainDataStruct)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Single_Struct",
                    functionName: "WriteStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, single, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Array_Byte()
        {
            byte[] data = (byte[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Array_Byte",
                    functionName: "WriteArrayByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, array, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Array_Float()
        {
            float[] data = (float[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Array_Float",
                    functionName: "WriteArrayFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, array, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Array_String()
        {
            string[] data = (string[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Array_String",
                    functionName: "WriteArrayString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, array, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Array_Class()
        {
            BenchmarkGrainDataClass[] data = (BenchmarkGrainDataClass[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Array_Class",
                    functionName: "WriteArrayClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, array, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Array_Struct()
        {
            BenchmarkGrainDataStruct[] data = (BenchmarkGrainDataStruct[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Array_Struct",
                    functionName: "WriteArrayStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, array, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Dictionary_Byte()
        {
            Dictionary<string, byte> data = (Dictionary<string, byte>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Dictionary_Byte",
                    functionName: "WriteDictionaryByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, dictionary, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Dictionary_Float()
        {
            Dictionary<string, float> data = (Dictionary<string, float>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Dictionary_Float",
                    functionName: "WriteDictionaryFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, dictionary, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Dictionary_String()
        {
            Dictionary<string, string> data = (Dictionary<string, string>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Dictionary_String",
                    functionName: "WriteDictionaryString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, dictionary, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Dictionary_Class()
        {
            Dictionary<string, BenchmarkGrainDataClass> data = (Dictionary<string, BenchmarkGrainDataClass>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Dictionary_Class",
                    functionName: "WriteDictionaryClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, dictionary, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_Dictionary_Struct()
        {
            Dictionary<string, BenchmarkGrainDataStruct> data = (Dictionary<string, BenchmarkGrainDataStruct>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_Dictionary_Struct",
                    functionName: "WriteDictionaryStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, dictionary, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_List_Byte()
        {
            List<byte> data = (List<byte>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_List_Byte",
                    functionName: "WriteListByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, list, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_List_Float()
        {
            List<float> data = (List<float>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_List_Float",
                    functionName: "WriteListFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, list, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_List_String()
        {
            List<string> data = (List<string>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_List_String",
                    functionName: "WriteListString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, list, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_List_Class()
        {
            List<BenchmarkGrainDataClass> data = (List<BenchmarkGrainDataClass>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_List_Class",
                    functionName: "WriteListClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, list, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_CG_W_List_Struct()
        {
            List<BenchmarkGrainDataStruct> data = (List<BenchmarkGrainDataStruct>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "CG_W_List_Struct",
                    functionName: "WriteListStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "cg, write, list, struct");
        }
        #endregion
        #region Grain->Grain Local Read
        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Single_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Single_Byte",
                    functionName: "ReadByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, single, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Single_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Single_Float",
                    functionName: "ReadFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, single, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Single_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Single_String",
                    functionName: "ReadString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, single, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Single_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Single_Class",
                    functionName: "ReadClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, single, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Single_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Single_Struct",
                    functionName: "ReadStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, single, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Array_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Array_Byte",
                    functionName: "ReadArrayByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, array, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Array_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Array_Float",
                    functionName: "ReadArrayFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, array, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Array_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Array_String",
                    functionName: "ReadArrayString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, array, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Array_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Array_Class",
                    functionName: "ReadArrayClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, array, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Array_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Array_Struct",
                    functionName: "ReadArrayStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, array, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Dictionary_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Dictionary_Byte",
                    functionName: "ReadDictionaryByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, dictionary, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Dictionary_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Dictionary_Float",
                    functionName: "ReadDictionaryFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, dictionary, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Dictionary_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Dictionary_String",
                    functionName: "ReadDictionaryString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, dictionary, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Dictionary_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Dictionary_Class",
                    functionName: "ReadDictionaryClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, dictionary, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_Dictionary_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_Dictionary_Struct",
                    functionName: "ReadDictionaryStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, dictionary, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_List_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_List_Byte",
                    functionName: "ReadListByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, list, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_List_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_List_Float",
                    functionName: "ReadListFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, list, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_List_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_List_String",
                    functionName: "ReadListString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, list, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_List_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_List_Class",
                    functionName: "ReadListClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, list, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_R_List_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_R_List_Struct",
                    functionName: "ReadListStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, read, list, struct");
        }

        #endregion
        #region Grain->Grain Local Write

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Single_Byte()
        {
            byte data = (byte)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Single_Byte",
                    functionName: "WriteByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, single, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Single_Float()
        {
            float data = (float)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Single_Float",
                    functionName: "WriteFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, single, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Single_String()
        {
            string data = (string)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Single_String",
                    functionName: "WriteString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, single, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Single_Class()
        {
            BenchmarkGrainDataClass data = (BenchmarkGrainDataClass)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Single_Class",
                    functionName: "WriteClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, single, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Single_Struct()
        {
            BenchmarkGrainDataStruct data = (BenchmarkGrainDataStruct)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Single_Struct",
                    functionName: "WriteStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, single, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Array_Byte()
        {
            byte[] data = (byte[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Array_Byte",
                    functionName: "WriteArrayByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, array, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Array_Float()
        {
            float[] data = (float[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Array_Float",
                    functionName: "WriteArrayFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, array, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Array_String()
        {
            string[] data = (string[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Array_String",
                    functionName: "WriteArrayString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, array, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Array_Class()
        {
            BenchmarkGrainDataClass[] data = (BenchmarkGrainDataClass[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Array_Class",
                    functionName: "WriteArrayClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, array, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Array_Struct()
        {
            BenchmarkGrainDataStruct[] data = (BenchmarkGrainDataStruct[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Array_Struct",
                    functionName: "WriteArrayStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, array, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Dictionary_Byte()
        {
            Dictionary<string, byte> data = (Dictionary<string, byte>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Dictionary_Byte",
                    functionName: "WriteDictionaryByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, dictionary, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Dictionary_Float()
        {
            Dictionary<string, float> data = (Dictionary<string, float>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Dictionary_Float",
                    functionName: "WriteDictionaryFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, dictionary, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Dictionary_String()
        {
            Dictionary<string, string> data = (Dictionary<string, string>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Dictionary_String",
                    functionName: "WriteDictionaryString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, dictionary, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Dictionary_Class()
        {
            Dictionary<string, BenchmarkGrainDataClass> data = (Dictionary<string, BenchmarkGrainDataClass>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Dictionary_Class",
                    functionName: "WriteDictionaryClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, dictionary, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_Dictionary_Struct()
        {
            Dictionary<string, BenchmarkGrainDataStruct> data = (Dictionary<string, BenchmarkGrainDataStruct>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_Dictionary_Struct",
                    functionName: "WriteDictionaryStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, dictionary, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_List_Byte()
        {
            List<byte> data = (List<byte>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_List_Byte",
                    functionName: "WriteListByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, list, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_List_Float()
        {
            List<float> data = (List<float>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_List_Float",
                    functionName: "WriteListFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, list, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_List_String()
        {
            List<string> data = (List<string>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_List_String",
                    functionName: "WriteListString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, list, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_List_Class()
        {
            List<BenchmarkGrainDataClass> data = (List<BenchmarkGrainDataClass>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_List_Class",
                    functionName: "WriteListClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, list, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGL_W_List_Struct()
        {
            List<BenchmarkGrainDataStruct> data = (List<BenchmarkGrainDataStruct>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: true,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGL_W_List_Struct",
                    functionName: "WriteListStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggl, write, list, struct");
        }

        #endregion
        #region Grain->Grain Remote Read

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Single_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Single_Byte",
                    functionName: "ReadByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, single, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Single_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Single_Float",
                    functionName: "ReadFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, single, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Single_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Single_String",
                    functionName: "ReadString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, single, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Single_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Single_Class",
                    functionName: "ReadClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, single, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Single_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Single_Struct",
                    functionName: "ReadStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, single, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Array_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Array_Byte",
                    functionName: "ReadArrayByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, array, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Array_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Array_Float",
                    functionName: "ReadArrayFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, array, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Array_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Array_String",
                    functionName: "ReadArrayString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, array, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Array_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Array_Class",
                    functionName: "ReadArrayClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, array, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Array_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Array_Struct",
                    functionName: "ReadArrayStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, array, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Dictionary_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Dictionary_Byte",
                    functionName: "ReadDictionaryByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, dictionary, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Dictionary_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Dictionary_Float",
                    functionName: "ReadDictionaryFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, dictionary, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Dictionary_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Dictionary_String",
                    functionName: "ReadDictionaryString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, dictionary, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Dictionary_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Dictionary_Class",
                    functionName: "ReadDictionaryClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, dictionary, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_Dictionary_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_Dictionary_Struct",
                    functionName: "ReadDictionaryStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, dictionary, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_List_Byte()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_List_Byte",
                    functionName: "ReadListByte",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, list, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_List_Float()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_List_Float",
                    functionName: "ReadListFloat",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, list, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_List_String()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_List_String",
                    functionName: "ReadListString",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, list, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_List_Class()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_List_Class",
                    functionName: "ReadListClass",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, list, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_R_List_Struct()
        {
            int messageSize = 100;

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_R_List_Struct",
                    functionName: "ReadListStruct",
                    data: new object[] { messageSize },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, read, list, struct");
        }

        #endregion
        #region Grain->Grain Remote Write

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Single_Byte()
        {
            byte data = (byte)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Single_Byte",
                    functionName: "WriteByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, single, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Single_Float()
        {
            float data = (float)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Single_Float",
                    functionName: "WriteFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, single, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Single_String()
        {
            string data = (string)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Single_String",
                    functionName: "WriteString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, single, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Single_Class()
        {
            BenchmarkGrainDataClass data = (BenchmarkGrainDataClass)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Single_Class",
                    functionName: "WriteClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, single, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Single_Struct()
        {
            BenchmarkGrainDataStruct data = (BenchmarkGrainDataStruct)(BenchmarkGrainDataClass.CreateData(DataContainerMode.NoContainerMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Single_Struct",
                    functionName: "WriteStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, single, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Array_Byte()
        {
            byte[] data = (byte[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Array_Byte",
                    functionName: "WriteArrayByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, array, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Array_Float()
        {
            float[] data = (float[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Array_Float",
                    functionName: "WriteArrayFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, array, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Array_String()
        {
            string[] data = (string[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Array_String",
                    functionName: "WriteArrayString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, array, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Array_Class()
        {
            BenchmarkGrainDataClass[] data = (BenchmarkGrainDataClass[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Array_Class",
                    functionName: "WriteArrayClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, array, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Array_Struct()
        {
            BenchmarkGrainDataStruct[] data = (BenchmarkGrainDataStruct[])(BenchmarkGrainDataClass.CreateData(DataContainerMode.ArrayMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Array_Struct",
                    functionName: "WriteArrayStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, array, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Dictionary_Byte()
        {
            Dictionary<string, byte> data = (Dictionary<string, byte>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Dictionary_Byte",
                    functionName: "WriteDictionaryByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, dictionary, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Dictionary_Float()
        {
            Dictionary<string, float> data = (Dictionary<string, float>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Dictionary_Float",
                    functionName: "WriteDictionaryFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, dictionary, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Dictionary_String()
        {
            Dictionary<string, string> data = (Dictionary<string, string>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Dictionary_String",
                    functionName: "WriteDictionaryString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, dictionary, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Dictionary_Class()
        {
            Dictionary<string, BenchmarkGrainDataClass> data = (Dictionary<string, BenchmarkGrainDataClass>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Dictionary_Class",
                    functionName: "WriteDictionaryClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, dictionary, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_Dictionary_Struct()
        {
            Dictionary<string, BenchmarkGrainDataStruct> data = (Dictionary<string, BenchmarkGrainDataStruct>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.DictionaryMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_Dictionary_Struct",
                    functionName: "WriteDictionaryStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, dictionary, struct");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_List_Byte()
        {
            List<byte> data = (List<byte>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.ByteMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_List_Byte",
                    functionName: "WriteListByte",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, list, byte");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_List_Float()
        {
            List<float> data = (List<float>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.FloatMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_List_Float",
                    functionName: "WriteListFloat",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, list, float");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_List_String()
        {
            List<string> data = (List<string>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.StringMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_List_String",
                    functionName: "WriteListString",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, list, string");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_List_Class()
        {
            List<BenchmarkGrainDataClass> data = (List<BenchmarkGrainDataClass>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.ClassMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_List_Class",
                    functionName: "WriteListClass",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, list, class");
        }

        [TestMethod, TestCategory("Benchmarks")]
        public void Benchmark_GGR_W_List_Struct()
        {
            List<BenchmarkGrainDataStruct> data = (List<BenchmarkGrainDataStruct>)(BenchmarkGrainDataClass.CreateData(DataContainerMode.ListMode, DataTypeMode.StructMode, 100));

            double timeActual =
                RunBenchmarkIntergrainReflected(
                    sameSilo: false,
                    numIterations: 500,
                    numDropIterations: 5,
                    testName: "GGR_W_List_Struct",
                    functionName: "WriteListStruct",
                    data: new object[] { data },
                    timeThreshold: 0.00);

            LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "ggr, write, list, struct");
        }

        #endregion

        #region BenchmarkGrains
        //[TestMethod, TestCategory("Benchmarks")]
        //public void BenchmarkCreateGrain()
        //{
        //    // Configurables
        //    int silos = 2;
        //    int drop = 100;
        //    int runs = 1000;
        //    double timeThreshold = 0.0;
            
        //    Stopwatch s = new Stopwatch();
        //    for (int i = 0; i < drop + runs; i++)
        //    {
        //        if (i >= drop)
        //            s.Start();

        //        IBenchmarkGrain benchmarkGrain = BenchmarkGrainFactory.CreateGrain(
        //            Name: Guid.NewGuid().ToString(),
        //            Strategies: new[] { GrainStrategy.PartitionPlacement(i % silos, silos) });

        //        benchmarkGrain.Wait();

        //        if (i >= drop)
        //            s.Stop();
        //    }
            
        //    double timeActual = s.Elapsed.TotalMilliseconds / runs;

        //    LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "");
            
        //    if (timeThreshold > 0.0)
        //        if (timeActual > timeThreshold)
        //            throw new Exception(string.Format("CreateGrain test was over the threshold: Actual {0:F3}ms, expected {1:F3}ms",
        //                timeActual, timeThreshold));
        //}

        //[TestMethod, TestCategory("Benchmarks")]
        //public void BenchmarkLookupGrainValid()
        //{
        //    // Configurables
        //    int silos = 2;
        //    int drop = 5;
        //    int runs = 25;
        //    int lookups = 100;
        //    double timeThreshold = 0.0;

        //    Stopwatch s = new Stopwatch();
        //    for (int i = 0; i < drop + runs; i++)
        //    {
        //        IBenchmarkGrain benchmarkGrain = BenchmarkGrainFactory.CreateGrain(
        //            Name: Guid.NewGuid().ToString(),
        //            Strategies: new[] { GrainStrategy.PartitionPlacement(i % silos, silos) });
        //        benchmarkGrain.Wait();

        //        string validId = benchmarkGrain.Name.Result;

        //        if (i >= drop)
        //            s.Start();

        //        for (int j = 0; j < lookups; j++)
        //            BenchmarkGrainFactory.Where(x => x.Name == validId).Wait();

        //        if (i >= drop)
        //            s.Stop();
        //    }

        //    double timeActual = s.Elapsed.TotalMilliseconds / (runs * lookups);

        //    LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "");

        //    if (timeThreshold > 0.0)
        //        if (timeActual > timeThreshold)
        //            throw new Exception(string.Format("CreateGrain test was over the threshold: Actual {0:F3}ms, expected {1:F3}ms",
        //                timeActual, timeThreshold));
        //}

        //[TestMethod, TestCategory("Benchmarks")]
        //public void BenchmarkLookupGrainInvalid()
        //{
        //    // Configurables
        //    int silos = 2;
        //    int drop = 5;
        //    int runs = 25;
        //    int lookups = 200;
        //    double timeThreshold = 0.0;

        //    Stopwatch s = new Stopwatch();
        //    for (int i = 0; i < drop + runs; i++)
        //    {
        //        IBenchmarkGrain benchmarkGrain = BenchmarkGrainFactory.CreateGrain(
        //            Name: Guid.NewGuid().ToString(),
        //            Strategies: new[] { GrainStrategy.PartitionPlacement(i % silos, silos) });
        //        benchmarkGrain.Wait();

        //        string invalidId = "Not a valid grain ID";

        //        if (i >= drop)
        //            s.Start();
                
        //        for (int j = 0; j < lookups; j++)
        //        {
        //            try
        //            {
        //                BenchmarkGrainFactory.Where(x => x.Id == invalidId).Wait();
        //            }
        //            catch (Exception) { }
        //        }
                
        //        if (i >= drop)
        //            s.Stop();
        //    }

        //    double timeActual = s.Elapsed.TotalMilliseconds / (runs * lookups);

        //    LogMetric("Runtime", string.Format("{0:F4}", timeActual), categories: "");

        //    if (timeThreshold > 0.0)
        //        if (timeActual > timeThreshold)
        //            throw new Exception(string.Format("CreateGrain test was over the threshold: Actual {0:F3}ms, expected {1:F3}ms",
        //                timeActual, timeThreshold));
        //}
        #endregion BenchmarkGrains

        #region ThreadRing Benchmark
        [TestMethod, TestCategory("Benchmarks")]
        public async Task BenchmarkThreadRing()
        {
            // ThreadRing benchmark for actor message passing performance
            // http://benchmarksgame.alioth.debian.org/u64q/performance.php?test=threadring#about

            // Configurables
            int ringSize = 503;
            int numHops = 1000;
            int runs = 1;

            ThreadRingWatcher watcher = new ThreadRingWatcher();
            IThreadRingWatcher observer = await ThreadRingWatcherFactory.CreateObjectReference(watcher);

            IThreadRingGrain[] grains = new IThreadRingGrain[ringSize];
            // Initialize thread ring
            for (int i = 0; i < ringSize; i++)
            {
                grains[i] = ThreadRingGrainFactory.GetGrain(i);
            }
            for (int i = 0; i < ringSize - 1; i++)
            {
                await Task.WhenAll(
                    grains[i].SetWatcher(observer),
                    grains[i].SetNeighbor(grains[i + 1]));
            }
            // Connect last to first in ring
            await Task.WhenAll(
                grains[ringSize-1].SetWatcher(observer),
                grains[ringSize-1].SetNeighbor(grains[0]));

            // Start thread ring
            IThreadRingGrain firstGrain = grains[0];
            var token = new ThreadRingToken { HopLimit = numHops };
            string what = "ThreadRing: M=" + ringSize + " N=" + numHops;

            TimeSpan runTime = TimeRun(runs, TimeSpan.Zero, what,
                () => {
                    firstGrain.PassToken(token).Wait();
                });

            var finalToken = watcher.FinalToken;

            Assert.IsNotNull(finalToken, "Should have received final token at time " + runTime);
            Console.WriteLine("{0} completed in {1} after {2} hops with Owner={3}", what, runTime, finalToken.HopCount, finalToken.Owner);
        }
        #endregion
    }

    public class ThreadRingWatcher : IThreadRingWatcher
    {
        public ThreadRingToken FinalToken { get; private set; }

        public void FinishedTokenRing(ThreadRingToken token)
        {
            this.FinalToken = token;
        }
    }

    [Serializable]
    public class BenchmarkTestDataClass
    {
        public string bigString = "";
        public List<string> stringList = new List<string>();

        public void CreateString(int size)
        {
            // 100 characters
            string baseString = "1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890";

            StringBuilder b = new StringBuilder(baseString);

            // double the stringBuilder length each time
            while (b.Length < size)
                b.Append(b);
            
            // cut back down to desired size
            bigString = b.ToString().Substring(0, size);
        }

        public void CreateEmptyStringList(int size)
        {
            // This ought to take up plenty of memory, but be trivially small when serialized
            stringList = new List<string>(size);
        }

        public void CreateFullStringList(int size)
        {
            stringList = new List<string>(size);
            for (int i=0; i<size; i++)
                stringList.Add("a");
        }
    }
}
