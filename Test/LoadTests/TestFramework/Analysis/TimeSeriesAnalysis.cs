using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;
using System.IO;
using System.Timers;
using System.Threading;

namespace Orleans.TestFramework
{
    #region Metrics
    /// <summary>
    /// Defines a Key Value pair, the value is supposed to calculated based on other data
    /// </summary>
    abstract public class Metric
    {
        /// <summary>
        /// Name of the metric
        /// </summary>
        public string Name;

        internal static double Parse(object obj)
        {
            if (obj is double) return (double)obj;
            else return double.Parse(obj.ToString().Trim());
        }
    }
    /// <summary>
    /// Base class for all the calculated metrics
    /// </summary>
    abstract public class DerivativeMetric : Metric
    {
        /// <summary>
        /// The metric on which is it based on 
        /// </summary>
        public string BasedOn;

        /// <summary>
        /// Size of the window
        /// </summary>
        public int WindowSize;

        /// <summary>
        /// What pass this metric should be calculated in { 0 to 3}
        /// </summary>
        public int Pass;

        /// <summary>
        /// How this metric calculated ?
        ///     Global : one datapoint is taken from each sender, and metric calculated across them
        ///     Moving : The window moves one item at a time
        ///         so for window size of 3 we have [1,2,3,4,5,6] => [1,2,3] -> [2,3,4] -> [3,4,5]
        ///     Period : The window jumps 
        ///         so for window size of 3 we have [1,2,3,4,5,6] => [1,2,3] -> [4,5,6]
        /// </summary>
        public string Scope;

        /// <summary>
        /// Last calculated value
        /// </summary>
        object lastValue;
        /// <summary>
        /// 
        /// </summary>
        int counter;
        /// <summary>
        /// Calculate the metric based on given data.
        /// <param name="data"> The data on which this metric is to be calculated</param>
        /// </summary>
        protected virtual object Calculate(IEnumerable<Record> data)
        {
            return null;
        }

        public object GetValue(IEnumerable<Record> data)
        {
            if (Scope != "Period" || (counter % this.WindowSize == 0))
            {
                // calculate fresh value only if window moving or it is time to jump
                lastValue = Calculate(data);
            }
            counter++;
            return lastValue;
        }
    }

    /// <summary>
    /// Creates average
    /// </summary>
    public class AverageMetric : DerivativeMetric
    {
        protected override object Calculate(IEnumerable<Record> data)
        {
            double total = 0;
            int n = 0;
            foreach (Record r in data)
            {
                if (r.ContainsKey(this.BasedOn))
                {
                    total += Parse(r[this.BasedOn]);
                    n++;
                }
            }
            if (n > 0)
            {
                double average = total / n;
                return average;
            }
            return null;
        }
    }
    /// <summary>
    /// Creates a percentile metric
    /// </summary>
    public class PercentileMetric : DerivativeMetric
    {
        /// <summary>
        /// The percentile value to check
        /// </summary>
        public double Percentile;
        protected override object Calculate(IEnumerable<Record> data)
        {
            List<double> working = new List<double>();
            foreach (Record r in data)
            {
                if (r.ContainsKey(this.BasedOn))
                {
                    working.Add((double)r[this.BasedOn]);
                }
            }
            if (working.Count > 0)
            {
                working.Sort();
                int index = (int)Math.Floor(working.Count * Percentile);
                double nth = working[index];
                return nth;
            }
            else
            {
                return null;
            }
        }
    }
    /// <summary>
    /// Simply add all the values
    /// </summary>
    public class AggregateMetric : DerivativeMetric
    {
        protected override object Calculate(IEnumerable<Record> data)
        {
            double total = 0;
            int n = 0;
            foreach (Record r in data)
            {
                if (r.ContainsKey(this.BasedOn))
                {
                    total += Parse(r[this.BasedOn]);
                    n++;
                }
            }
            if (n > 0)
            {
                return total;
            }
            return null;
        }
    }

    /// <summary>
    /// Min of all the values
    /// </summary>
    public class MinMetric : DerivativeMetric
    {
        protected override object Calculate(IEnumerable<Record> data)
        {
            double min = 0;
            int n = 0;
            foreach (Record r in data)
            {
                if (r.ContainsKey(this.BasedOn))
                {
                    var val = Parse(r[this.BasedOn]);
                    if(min==0) min = val;
                    else min = Math.Min(min, val);
                    n++;
                }
            }
            if (n > 0)
            {
                return min;
            }
            return null;
        }
    }


    /// <summary>
    /// Max of all the values
    /// </summary>
    public class MaxMetric : DerivativeMetric
    {
        protected override object Calculate(IEnumerable<Record> data)
        {
            double max = 0;
            int n = 0;
            foreach (Record r in data)
            {
                if (r.ContainsKey(this.BasedOn))
                {
                    max = Math.Max(max, Parse(r[this.BasedOn]));
                    n++;
                }
            }
            if (n > 0)
            {
                return max;
            }
            return null;
        }
    }

    /// <summary>
    /// Count of all the values
    /// </summary>
    public class CountMetric : DerivativeMetric
    {
        protected override object Calculate(IEnumerable<Record> data)
        {
            int n = 0;
            foreach (Record r in data)
            {
                if (r.ContainsKey(this.BasedOn))
                {
                    n++;
                }
            }
            if (n > 0)
            {
                return n;
            }
            return null;
        }
    }
    #endregion
    #region Asserts
    /// <summary>
    /// Base class
    /// </summary>
    abstract public class MetricAssert
    {
        /// <summary>
        /// Name of the metric to Assert on
        /// </summary>
        public string BasedOn;
        /// <summary>
        /// Friendly description
        /// </summary>
        public string Description = "";

        /// <summary>
        /// Applied gloablly or per sender
        /// </summary>
        public bool IsGlobal;

        /// <summary>
        /// Size of the window
        /// </summary>
        public int WindowSize;

        /// <summary>
        /// Ability to scale the numbers based on environment
        /// </summary>
        public string ScaleBy;

        /// <summary>
        /// This method is supposed to throw an exception when assertion fails
        /// </summary>
        public virtual void Check(MetricCollector collector, IEnumerable<Record> window)
        {
        }
    }
    /// <summary>
    /// Simple watermark assertion that checks if metric value is within the bound for all values in the window.
    /// When strict =true , all values must be within bound
    /// otherwise at least one value must be in the bound
    /// </summary>
    public class MetricWatermarkAssert : MetricAssert
    {
        /// <summary>
        /// Lowest allowed value
        /// </summary>
        public double LowWatermark;

        /// <summary>
        /// Highest allowed value
        /// </summary>
        public double HighWatermark;

        /// <summary>
        /// If true ALL values must be within this range
        /// if false AT LEAST 1 value must be within this range
        /// </summary>
        public bool Strict;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="collector"></param>
        /// <param name="window"></param>
        public override void Check(MetricCollector collector, IEnumerable<Record> window)
        {
            double scale = collector.GetVariableValue(ScaleBy, 1.0);
            double low = scale * LowWatermark;
            double high = scale * HighWatermark;
            int n = 0;
            // don't bother if metric is not calculated yet
            bool valueInRange = false;
            foreach (Record r in window)
            {
                if (r.ContainsKey(this.BasedOn))
                {
                    double value = Metric.Parse(r[this.BasedOn]);
                    if (value < high && value > low)
                    {
                        valueInRange = true;
                    }
                    else
                    {
                        if (Strict)
                        {
                            String str = String.Format("Metric Assert Failed:Some Values out of bound.\n\t{0} \n\t High:{1} Low:{2} Window:{3} \n\tData={4}",
                                Description,
                                high,
                                low,
                                WindowSize,
                                ToString(window));
                            //Log.WriteLine(SEV.STATUS, "MetricWatermarkAssert", str);
                            Assert.Fail(str);
                        }
                    }
                    n++;
                }
                
            }
            if (n > 0)
            {
                String str = String.Format("Metric Assert Failed:All Values out of bound.\n\t{0} \n\t High:{1} Low:{2} Window:{3} \n\tData={4}",
                                Description,
                                high,
                                low,
                                WindowSize,
                                ToString(window));
                //Log.WriteLine(SEV.STATUS, "MetricWatermarkAssert", str);
                Assert.IsTrue(valueInRange, str);
            }
        }
        /// <summary>
        /// Helper
        /// </summary>
        /// <param name="window"></param>
        /// <returns></returns>
        private string ToString(IEnumerable<Record> window)
        { 
            StringBuilder ret = new StringBuilder("[");
            foreach(Record r in window)
            {
                if (r.ContainsKey(this.BasedOn))
                {
                    ret.AppendFormat(r[this.BasedOn].ToString());
                }
                else
                {
                    ret.AppendFormat("null");
                }
                ret.AppendFormat(",");
            }
            ret.AppendFormat("];");
            return ret.ToString();
        }
    }

    #endregion
    #region collection logic
    /// <summary>
    /// This class respresents the Metric collection
    /// </summary>
    public class MetricCollector
    {
        /// <summary>
        /// Name of the collector
        /// </summary>
        public string Name { get; set; }
        public MetricCollector(int globalbuffer = 1000, int senderBuffer = 100, bool start = false)
        {
            this.globalbuffer = globalbuffer;
            this.senderBuffer = senderBuffer;
            
            if (start)
            {
                BeginAnalysis();
            }
        }
        /// <summary>
        /// Is this collector running
        /// </summary>
        bool isActive = false;
        /// <summary>
        /// Can it exit monitoring
        /// </summary>
        bool canExit = false;
        /// <summary>
        /// Should the collector exit early
        /// </summary>
        public bool ExitEarly { get; set; }
        /// <summary>
        /// How many datapoints it must collect
        /// </summary>
        public static int EarlyResultCount = 40;

        /// <summary>
        /// Little house keeping data on how many lines we have printed
        /// </summary>
        int linePrinted = 0;
        
        /// <summary>
        /// Begins calculating metrics
        /// </summary>
        public void BeginAnalysis()
        {
            isActive = true;
        }
        /// <summary>
        /// Ends calculating metrics
        /// </summary>
        public void EndAnalysis()
        {
            // workaround for a race condition that prevents statistics from being collected on really short tests.
            Thread.Sleep(TimeSpan.FromSeconds(10));
            isActive = false;
            // throw away all the data.
            foreach (string sender in dataPointsBySender.Keys)
            { 
                lock(dataPointsBySender[sender])
                {
                    dataPointsBySender[sender].Clear();
                }
            }
            linePrinted = 0;
            currentIndex = 0;
            canExit = false;
        }

        /// <summary>
        /// Adds a sender of data
        /// </summary>
        /// <param name="sender"></param>
        public void AddSender(string sender)
        {
            lock (dataPointsBySender)
            {
                dataPointsBySender.Add(sender, new List<Record>());
            }
        }

        /// <summary>
        /// For defining runtime variables used during metric calculations
        /// </summary>
        private Dictionary<string, double> variables = new Dictionary<string, double>();
        
        /// <summary>
        /// Add variable
        /// </summary>
        /// <param name="name">name of the variable</param>
        /// <param name="value">value of the variable</param>
        public void AddVariable(string name, double value)
        {
            lock (variables)
            {
                variables.Add(name, value);
            }
        }
        /// <summary>
        /// Chnage the existing variables
        /// </summary>
        /// <param name="name">name of the variable</param>
        /// <param name="value">new values</param>
        public void ChangeVariable(string name, double value)
        {
            lock (variables)
            {
                variables[name]=value;
            }
        }
        /// <summary>
        /// Get variable value if defined or return default value
        /// </summary>
        /// <param name="name"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public double GetVariableValue(string name, double defaultValue)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (variables.ContainsKey(name))
                {
                    return variables[name];
                }
            }
            return defaultValue;
        }

        /// <summary>
        /// List of Metrics to derive based on data
        /// </summary>
        public List<DerivativeMetric> Metrics = new List<DerivativeMetric>();
        /// <summary>
        /// List of Assertions to run against the data
        /// </summary>
        public List<MetricAssert> Asserts = new List<MetricAssert>();

        #region Processing
        /// <summary>
        /// OVERALL DESIGN 
        /// [1] We maintain a separate queue for each sender (files in our case).
        ///     file1 = [ a, b, c]
        ///     file2 = [ p ] 
        ///     file2 = [ x, y] 
        /// [2] Not all senders create data at equal rates - in the data above we can process ONLY 1 set of data GLOBALLY because  data for second row is not available yet.
        /// [3] Anyway we need to keep a certain level of past data so that we can calculate historical data (like moving averages).
        /// [4] The currentIndex indicates how many rows have been processed so far
        /// [5] Periodically we'll delete all the data that is lready processed and reset the counters
        /// </summary>
        Dictionary<string, List<Record>> dataPointsBySender = new Dictionary<string, List<Record>>();
        /// <summary>
        /// Max number of values to keep globally
        /// </summary>
        int globalbuffer;
        /// <summary>
        /// Max number of values to keep per sender
        /// </summary>
        int senderBuffer;

        /// <summary>
        /// This method is called by the parser after reciving a data point
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="data"></param>
        public void OnDataRecieved(string sender, Record data)
        {
            if (!isActive) return;
            // for debugging
            data.Add("__source__", sender);
            data.Add("__recieved__", DateTime.UtcNow);

            // Save this data
            lock (dataPointsBySender[sender])
            {
                dataPointsBySender[sender].Add(data);
            }
        }

        /// <summary>
        /// How many items have been processed so far
        /// </summary>
        int currentIndex = 0;
        /// <summary>
        /// Thread safty
        /// </summary>
        bool isProcessing = false;
        /// <summary>
        /// Processes each data point
        /// 
        /// </summary>
        public void Process()
        {
            // first we should some have data to process :)
            if (dataPointsBySender.Count == 0) return;

            // One thread at a time only, if we are already processing then no-op
            if (isProcessing) return;
            lock (this)
            {
                if (isProcessing) return;
                isProcessing = true;
            }
            
            try
            {
                while (true)
                {
                    // -- STEP 1 : start processing as much as we can ---
                    
                    // This variable holds a single row across all the senders
                    List<Record> across = new List<Record>();

                    // see if any of the sender has added any new data. 
                    // which translates to any of the receivers having more Count than what we have already processed
                    foreach (string s in dataPointsBySender.Keys)
                    {
                        // skip processing IF we don't have enough data yet
                        if (dataPointsBySender[s].Count <= currentIndex) return;
                        across.Add(dataPointsBySender[s][currentIndex]);
                    }
                    // -- STEP 2 : calculate all the metrics for available data --
                    // we will come here only if we have next set of data from all sources.
                    // Now calculate PER SENDER metrics in 4 passes.
                    // Remember pass N can use values calculated in erlier passes.
                    for (int i = 0; i < 4; i++)
                    {
                        int passNum = i;
                        // First calculate PER SENDER metric
                        foreach (DerivativeMetric m in
                            from metric in Metrics where metric.Pass == passNum && metric.Scope != "Global" select metric)
                        {
                            // each sender
                            foreach (string s in dataPointsBySender.Keys) 
                            {
                                //sender specific queue
                                List<Record> window = dataPointsBySender[s];
                                if (currentIndex >= m.WindowSize - 1)
                                {
                                    // we have more historical data than required by metric's window, so we can calculate
                                    lock (window)
                                    {
                                        // pass appropriate range of data to metric, so that new value based on this raw data can be calculated.
                                        object value = m.GetValue(
                                            window.Skip(currentIndex - m.WindowSize + 1)
                                                .Take(m.WindowSize));
                                        if (null != value)
                                        {
                                            Assert.IsTrue(!string.IsNullOrWhiteSpace(m.Name));
                                            // assign this newly calculated metric value
                                            window[currentIndex][m.Name] = value;
                                        }
                                    }
                                }
                            }
                        }
                        // Then calculate aggregate or GLOBAL metrics that are accross the row
                        foreach (DerivativeMetric m in
                            from metric in Metrics where metric.Pass == passNum && metric.Scope == "Global" select metric)
                        {
                            // pass one datapoint each from each sender.
                            object value = m.GetValue(across);
                            if (null != value)
                            {
                                foreach (Record r in across)
                                {
                                    Assert.IsTrue(!string.IsNullOrWhiteSpace(m.Name));
                                    r[m.Name] = value;
                                }
                            }
                        }
                    }

                    // -- STEP 3 : don't forget to update how much we have processed so far :) 
                    currentIndex++;

                    // -- STEP 4 :  now that we have all the metrics we need to assert ---
                    foreach (MetricAssert a in Asserts)
                    {
                        if (a.IsGlobal)
                        {
                            a.Check(this, across);
                        }
                        else
                        {
                            // check non-global asserts for each sender
                            foreach (string s in dataPointsBySender.Keys) 
                            {
                                if ((currentIndex - a.WindowSize)>0)
                                {
                                    lock (dataPointsBySender[s])
                                    {
                                        a.Check(this, dataPointsBySender[s].Skip(currentIndex - a.WindowSize));
                                    }
                                }
                            }
                        }
                    }
                    // -- STEP 5 :  write out some data ---
                    var keys = from metric in Metrics where metric.Scope == "Global" select metric.Name;
                    if (linePrinted == 0)
                    {
                        if (Log.TestResults != null)
                        {
                            Log.TestResults.AddHeaders(keys);
                        }
                    }
                    
                    // Now save the data
                    if (Log.TestResults != null)
                    {
                        Log.TestResults.AddRecord(linePrinted, across[0]);
                    }

                    linePrinted++;
                    if (linePrinted == EarlyResultCount)
                    {
                        if (Log.TestResults != null)
                        {
                            //bool consoleOn = (linePrinted < EarlyResultCount);
                            Log.TestResults.CheckPointEarlyResults();
                        }
                        canExit = true;
                    }

                    // -- STEP 5 :  Delete all the data that is not needed ---
                    while (currentIndex > this.senderBuffer)
                    {
                        foreach (string s in dataPointsBySender.Keys)
                        {
                            lock (dataPointsBySender[s])
                            {
                                dataPointsBySender[s].RemoveAt(0);
                            }
                        }
                        currentIndex--;
                    }
                }
            }
            finally
            {
                lock (this)
                {
                    isProcessing = false;
                }
            }
        }
        
        /// <summary>
        /// Call this function to process the collected data from the background thread.
        /// </summary>
        public void ProcessInBackground()
        {
            while (isActive)
            {
                Process();
                if (ExitEarly && canExit) return;
                Thread.Sleep(500);
            }
        }
        #endregion
    }
#endregion
    
}
