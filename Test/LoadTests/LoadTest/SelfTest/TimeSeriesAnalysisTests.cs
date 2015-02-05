using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;

namespace Orleans.TestFramework.SelfTest
{
    [TestClass]
    public class TimeSeriesAnalysisTests
    {
        [TestMethod]
        public void TestFrameworkTest_SimpleMovingAverage()
        {
            MetricCollector collector = new MetricCollector(100,10,false);
            AverageMetric movingAverage = new AverageMetric();
            movingAverage.Name = "Test";
            movingAverage.BasedOn = "Data";
            movingAverage.Scope = "Moving";
            movingAverage.WindowSize = 2;
            collector.Metrics.Add(movingAverage);
            
            int[] data = new int[10];
            List<Record> dataPoints = new List<Record>();
            for (int i = 0; i < 10; i++)
            {
                data[i] = 1 + i;
                Record dataPoint = new Record();
                dataPoint.Add("Data", (double)data[i]);
                dataPoints.Add(dataPoint);
            }
            collector.AddSender("Foo");
            collector.BeginAnalysis();
            // Fire Event
            foreach (var dataPoint in dataPoints)
            {
                collector.OnDataRecieved("Foo", dataPoint);
            }
            collector.Process();
            double[] actual = new double[10];
            for (int i = 1; i < 10; i++)
            {
                actual[i] = (double)dataPoints[i]["Test"];
            }
            for (int i = 1; i < 10; i++)
            {
                double expected = ((double)(data[i] + data[i - 1])) / 2;
                Assert.AreEqual(expected, actual[i]);
            }
        }

        [TestMethod]
        public void TestFrameworkTest_SimpleMovingAverageForConstantData()
        {
            MetricCollector collector = new MetricCollector(100, 10, false);
            AverageMetric movingAverage = new AverageMetric();
            movingAverage.Name = "Test";
            movingAverage.BasedOn = "Data";
            movingAverage.Scope = "Moving";
            movingAverage.WindowSize = 3;
            collector.Metrics.Add(movingAverage);

            List<Record> dataPoints = new List<Record>();
            for (int i = 0; i < 10; i++)
            {
                Record dataPoint = new Record();
                dataPoint.Add("Data", 42.0);
                dataPoints.Add(dataPoint);
            }
            collector.AddSender("Foo");
            collector.BeginAnalysis();
            // Fire Event
            foreach (var dataPoint in dataPoints)
            {
                collector.OnDataRecieved("Foo", dataPoint);
            }
            collector.Process();
            for (int i = 3; i < 10; i++)
            {
                double actual = (double) dataPoints[i]["Test"];
                Assert.AreEqual(42, actual);
            }
        }
        [TestMethod]
        public void TestFrameworkTest_AverageMetricPeriod()
        {
            MetricCollector collector = new MetricCollector(100,10,false);
            AverageMetric periodAverage = new AverageMetric();
            periodAverage.Name = "Test";
            periodAverage.BasedOn = "Data";
            periodAverage.WindowSize = 2;
            periodAverage.Scope = "Period";
            collector.Metrics.Add(periodAverage);
            
            int[] data = new int[10];
            List<Record> dataPoints = new List<Record>();
            for (int i = 0; i < 10; i++)
            {
                data[i] = 1 + i;
                Record dataPoint = new Record();
                dataPoint.Add("Data", (double)data[i]);
                dataPoints.Add(dataPoint);
            }
            collector.AddSender("Foo");
            collector.BeginAnalysis();
            // Fire Event
            foreach (var dataPoint in dataPoints)
            {
                collector.OnDataRecieved("Foo", dataPoint);
            }
            collector.Process();
            double expected = 1.5;
            double[] actual = new double[10];
            for (int i = 1; i < 10; i++)
            {
                actual[i] = (double)dataPoints[i]["Test"];
            }
            for (int i = 1; i < 10; i++)
            {
                Assert.AreEqual(expected, actual[i]);
                if (i % 2 == 0)
                {
                    expected = expected + 2;
                }
                
            }
        }

        [TestMethod]
        public void TestFrameworkTest_AverageMetricForConstantData()
        {
            MetricCollector collector = new MetricCollector(100, 10, false);
            AverageMetric periodAverage = new AverageMetric();
            periodAverage.Name = "Test";
            periodAverage.BasedOn = "Data";
            periodAverage.WindowSize = 2;
            periodAverage.Scope = "Period";
            collector.Metrics.Add(periodAverage);

            List<Record> dataPoints = new List<Record>();
            for (int i = 0; i < 10; i++)
            {
                Record dataPoint = new Record();
                dataPoint.Add("Data", 42.0);
                dataPoints.Add(dataPoint);
            }
            collector.AddSender("Foo");
            collector.BeginAnalysis();
            // Fire Event
            foreach (var dataPoint in dataPoints)
            {
                collector.OnDataRecieved("Foo", dataPoint);
            }
            collector.Process();
            for (int i = 3; i < 10; i++)
            {
                double actual = (double) dataPoints[i]["Test"];
                Assert.AreEqual(42, actual);
            }
        }

        [TestMethod]
        public void TestFrameworkTest_PercentileMetricForConstantData()
        {
            for(int i = 10 ; i < 20 ; i++)
            {
                MetricCollector collector = new MetricCollector(100, 10, false);
                PercentileMetric percentile = new PercentileMetric();
                percentile.Name = "Test";
                percentile.BasedOn = "Data";
                percentile.WindowSize = 3;
                percentile.Percentile = 0.9;
                percentile.Scope = "Period";
                collector.Metrics.Add(percentile);

                List<Record> dataPoints = new List<Record>();
                for (int j = 0; j< i; j++)
                {
                    Record dataPoint = new Record();
                    dataPoint.Add("Data", 42.0);
                    dataPoints.Add(dataPoint);
                }
                collector.AddSender("Foo");
                collector.BeginAnalysis();
                // Fire Event
                foreach(var dataPoint in dataPoints)
                {
                    collector.OnDataRecieved("Foo", dataPoint);
                }
                collector.Process();
                for (int j = 10; j < i; j++)
                {
                    double actual = (double)dataPoints[j]["Test"];
                    Assert.AreEqual(42, actual);
                }
            }
        }

        [TestMethod]
        public void TestFrameworkTest_PercentileMetric()
        {
            MetricCollector collector = new MetricCollector(100, 10 , false);
            PercentileMetric percentile = new PercentileMetric();
            percentile.Name = "Test1";
            percentile.BasedOn = "Data";
            percentile.WindowSize = 10;
            percentile.Percentile = 0.9;
            percentile.Scope = "Period";
            collector.Metrics.Add(percentile);
            int n = 100;
            int[] data = new int[n];
            List<Record> dataPoints = new List<Record>();
            for (int i = 0; i < n; i++)
            {
                data[i] = i;
                Record dataPoint = new Record();
                dataPoint.Add("Data", (double)data[i]);
                dataPoints.Add(dataPoint);
            }
            collector.AddSender("Foo");
            // don't bother with early reasults.
            collector.ExitEarly = false;
            MetricCollector.EarlyResultCount = int.MaxValue; 
            collector.BeginAnalysis();
            // Fire Event
            foreach (var dataPoint in dataPoints)
            {
                collector.OnDataRecieved("Foo", dataPoint);
            }
            //var x = (from d in dataPoints.Skip(10) select ((double)d["Test1"])).ToList();
            collector.Process();
            for (int i = 10; i < n; i++)
            {
                double actual = (double)dataPoints[i]["Test1"];
                Assert.IsTrue(actual<i+1);
                Assert.IsTrue(actual > i-10);
                int temp = 10 * ((i + 1) / 10);
                Assert.IsTrue(actual+1 == temp);
            }
        }
        [TestMethod]
        public void TestFrameworkTest_WaterMark()
        {
            MetricCollector collector = new MetricCollector(100, 10, false);
            MetricWatermarkAssert assert = new MetricWatermarkAssert();
            assert.Description = "Desc.";
            assert.WindowSize = 3;
            assert.LowWatermark = 9;
            assert.HighWatermark = 20;
            assert.Strict = true;
            assert.BasedOn = "Data";
            collector.Asserts.Add(assert);
            int[] data = new int[10];
            List<Record> dataPoints = new List<Record>();
            for (int i = 0; i < 10; i++)
            {
                data[i] = 10 + i;
                Record dataPoint = new Record();
                dataPoint.Add("Data", (double)data[i]);
                dataPoints.Add(dataPoint);
            }
            collector.AddSender("Foo");
            collector.BeginAnalysis();
            // Fire Event
            foreach (var dataPoint in dataPoints)
            {
                collector.OnDataRecieved("Foo", dataPoint);
            }
            collector.Process();
        }

        [TestMethod]
        public void TestFrameworkTest_SimpleAggregate()
        {
            MetricCollector collector = new MetricCollector(100, 10, false);
            AggregateMetric agg = new AggregateMetric();
            agg.Name = "Test";
            agg.BasedOn = "Data";
            agg.Scope = "Global";
            agg.WindowSize = 10;
            collector.Metrics.Add(agg);

            int[] data = new int[10];
            for (int i = 0; i < 10; i++)
            {
                collector.AddSender("Foo"+i.ToString());
            }
            collector.BeginAnalysis();
            Dictionary<string,List<Record>> dataPoints = new Dictionary<string,List<Record>>();
            for (int i = 0; i < 10; i++)
            {
                dataPoints.Add("Foo" + i.ToString(), new List<Record>());
                for (double j = 0; j < 10; j++)
                {
                    Record dataPoint = new Record();
                    dataPoint.Add("Data", j);
                    dataPoints["Foo" + i.ToString()].Add(dataPoint);
                    collector.OnDataRecieved("Foo"+i.ToString(), dataPoint);
                }
            }
            collector.Process();
            
            for (int i = 0; i < 10; i++)
            {
                List<Record> lst = dataPoints["Foo" + i.ToString()];
                for (int j = 0; j < 10; j++)
                {
                    double actual = (double)lst[j]["Test"];
                    double expected = 10*j;
                    Assert.AreEqual(expected, actual);
                }
            }
        }

        [TestMethod]
        public void TestFrameworkTest_SimpleMinMax()
        {
            MetricCollector collector = new MetricCollector(100, 10, false);
            MinMetric min = new MinMetric();
            min.Name = "TestMin";
            min.BasedOn = "Data";
            min.Scope = "Global";
            min.WindowSize = 10;
            collector.Metrics.Add(min);

            MaxMetric max = new MaxMetric();
            max.Name = "TestMax";
            max.BasedOn = "Data";
            max.Scope = "Global";
            max.WindowSize = 10;
            collector.Metrics.Add(max);

            int[] data = new int[10];
            for (int i = 0; i < 10; i++)
            {
                collector.AddSender("Foo" + i.ToString());
            }
            collector.BeginAnalysis();
            Dictionary<string, List<Record>> dataPoints = new Dictionary<string, List<Record>>();
            for (int i = 0; i < 10; i++)
            {
                dataPoints.Add("Foo" + i.ToString(), new List<Record>());
                for (double j = 0; j < 10; j++)
                {
                    Record dataPoint = new Record();
                    dataPoint.Add("Data", i);   
                    dataPoints["Foo" + i.ToString()].Add(dataPoint);
                    collector.OnDataRecieved("Foo" + i.ToString(), dataPoint);
                }
            }
            collector.Process();

            for (int i = 0; i < 10; i++)
            {
                List<Record> lst = dataPoints["Foo" + i.ToString()];
                for (int j = 0; j < 10; j++)
                {
                    double actual = (double)lst[j]["TestMin"];
                    double expected = 1;
                    Assert.AreEqual(expected, actual);
                    
                    actual = (double)lst[j]["TestMax"];
                    expected = 9;
                    Assert.AreEqual(expected, actual);
                }
            }
        }

        [TestMethod]
        public void TestFrameworkTest_ExecuteWithTimeout()
        {
            try
            {
                TaskHelper.ExecuteWithTimeout(() => { Thread.Sleep(1000); }, TimeSpan.FromMilliseconds(100)).Wait();
            }
            catch (Exception exc)
            {
                if (exc.GetBaseException().GetType().Equals(typeof(TimeoutException)))
                    return;
            }
            Assert.Fail("The test failed - did not timeout as expected");
        }
    }
}
