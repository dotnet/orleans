using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Timers;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Orleans.TestFramework
{

    /// <summary>
    /// Observes the file and acts as a source of events.
    /// It periodically opens the file and reads newly added lines and sends an event to its observers.
    /// After a certain number of turns where file is not modified at all, it raises OnCompleted.
    /// This class allows filtering of lines by applying given lambda predicates.
    /// </summary>
    public class FileObserver : IObservable<List<string>>
    {
        /// <summary>
        /// Path of file to watch
        /// </summary>
        string fileName;
        /// <summary>
        /// Property for file to watch
        /// </summary>
        public string Source
        {
            get { return fileName; }
            set { fileName = value; }
        }
        /// <summary>
        /// True when OnCompleted even is fired.
        /// </summary>
        public bool Done { get; set; }

        #region File Counters, Positions etc
        /// <summary>
        /// Last time file was read by this class
        /// </summary>
        DateTime lastReadUtc;
        /// <summary>
        /// variable to used to figure out whether the file has been updated since we last checked.
        /// </summary>
        DateTime savedLastWriteUtc;
        /// <summary>
        /// variable to used to figure out whether the file has been updated since we last checked.
        /// </summary>
        long savedLastFileSize;
        /// <summary>
        /// last file pointer position, we will start reading fromState here onwards.
        /// </summary>
        long lastPosition;
        /// <summary>
        /// number of lines read so far.
        /// </summary>
        public long LinesCounter {get;private set;}
        #endregion 
        
        #region timer
        /// <summary>
        /// Millisecond between each check
        /// </summary>
        int miliseconds;
        /// <summary>
        /// Timer object used for callbacks
        /// </summary>
        Timer timer;
        /// <summary>
        /// max number of idle retries , after which we will declare file being done.
        /// </summary>
        int maxIdleRetries = 1000;

        /// <summary>
        /// min number of idle retries , after which we will callback to check if we should still continue.
        /// </summary>
        int minIdleRetries = 100;
        /// <summary>
        /// Retries so far. Reset after each successful read.
        /// </summary>
        int retries;
        /// <summary>
        /// Gives opprtunity to caller to check external terminating conditions.
        /// </summary>
        public Func<bool> terminationCheck; 
        #endregion        

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="fileName">path to file</param>
        /// <param name="miliseconds">time in miliseconds between consecutive checks</param>
        public FileObserver(string fileName, int miliseconds, int minIdleRetries, int maxIdleRetries ,  Func<bool> terminationCheck = null)
        {
            this.fileName = fileName;
            this.miliseconds = miliseconds;
            this.minIdleRetries = minIdleRetries;
            this.maxIdleRetries = maxIdleRetries;
            this.terminationCheck = terminationCheck;
            Start();
        }
        /// <summary>
        /// Notifies the provider that an observer is to receive notifications.
        /// </summary>
        /// <param name="observer">The object that is to receive notifications.</param>
        /// <returns>The observer's interface that enables resources to be disposed.</returns>
        public IDisposable Subscribe(IObserver<List<string>> observer)
        {
            return new Subscription(this, observer);
        }

        
        
#region Composition
        /// <summary>
        /// Only lines satisfying this condition/criteria will result in an event being fired. 
        /// </summary>
        Func<string, bool> filteringCriteria;
        /// <summary>
        /// No events fired after this condition is met.
        /// </summary>
        Func<string, bool> stoppingCriteria;
        /// <summary>
        /// No events fired until this condition is met.
        /// </summary>
        Func<string, bool> startingCriteria;
        //
        bool canStartAdding = true;
        
        /// <summary>
        /// Sets the filter for event stream
        /// </summary>
        /// <param name="predicate"></param>
        /// <returns></returns>
        public IObservable<List<string>> Where(Func<string, bool> predicate)
        {
            this.filteringCriteria = predicate;
            return this;
        }
        
        /// <summary>
        /// Sets the predicate that evaluates the ending criteria
        /// </summary>
        /// <param name="before">lambda</param>
        /// <returns></returns>
        public IObservable<List<string>> Before(Func<string, bool> before)
        {
            this.stoppingCriteria = before;
            return this;
        }
        
        /// <summary>
        /// Sets the predicate that evaluates the starting criteria
        /// </summary>
        /// <param name="before">lambda</param>
        /// <returns></returns>
        public IObservable<List<string>> After(Func<string, bool> after)
        {
            this.startingCriteria = after;
            canStartAdding = false; 
            return this;
        }
        /// <summary>
        /// For buffering. NOT IMPLEMENTED
        /// </summary>
        /// <param name="batchsizeMax"></param>
        /// <returns></returns>
        public IObservable<List<string>> WithBatchSize(uint batchsizeMax)
        {
            this.batchsize = (int)batchsizeMax;
            return this;
        }
        /// <summary>
        /// For buffering. NOT IMPLEMENTED
        /// </summary>
        /// <param name="batchsizeMin"></param>
        /// <param name="batchsizeMax"></param>
        /// <returns></returns>
        public IObservable<List<string>> WithBatchSizeBetween(uint batchsizeMin, uint batchsizeMax)
        {
            this.batchsize = (int)batchsizeMax;
            return this;
        }
#endregion
#region Processing
        /// <summary>
        /// The queue that stores batches of lines read everytime new data is available
        /// </summary>
        Queue<List<string>> batchesOflines = new Queue<List<string>>();
        /// <summary>
        /// used to guard against rentry
        /// </summary>
        bool isProcessing;
        /// <summary>
        /// used to guard against rentry
        /// </summary>
        bool isReading;

        /// <summary>
        /// Signals that observer needs to stop
        /// </summary>
        bool stopRequested;
        /// <summary>
        /// batch size used for .
        /// </summary>
        int batchsize = -1;
#endregion
        /// <summary>
        /// Starts the "watching"
        /// </summary>
        private void Start()
        {
            Cycle();
            timer = new Timer(miliseconds);
            timer.AutoReset = true;
            timer.Elapsed += new ElapsedEventHandler(OnTimer);
            timer.Start();
        }
        /// <summary>
        /// Stops the "watching"
        /// </summary>
        private void Stop()
        {
            try
            {
                timer.Stop();
                timer.Dispose();
                var subscribers = Subscription.GetSubscribers(this);
                if (null != subscribers)
                {
                    foreach (IObserver<List<string>> subscriber in subscribers.Cast<IObserver<List<string>>>())
                    {
                        try
                        {
                            subscriber.OnCompleted();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch(Exception )
            {
            }
        }
        /// <summary>
        /// Callback that is called by the timer
        /// </summary>
        /// <param name="source">timer</param>
        /// <param name="e">event</param>
        private void OnTimer(object source, ElapsedEventArgs e)
        {
            Cycle();
        }
        /// <summary>
        /// Two step cycle for each time timer is called
        /// </summary>
        private void Cycle()
        {
            var subscribers = Subscription.GetSubscribers(this);
            if (null != subscribers)
            {
                // Read file. which will enqueue the newly read lines.
                // If there is still an earlier ReadFile running some other thread, this read becomes a no-op.
                // However to avoid delays, we will still attempt to fire already queued events.
                ReadFile();

                // Events are queued by the ReadFile. FireEvent will call each and every observer one by one for each event.
                // There is no concept of timeouts here- if you bad in pass callback method -you'll hang yourself .
                // If there is still an earlier FireEvent running some other thread(called from earlier timer), this read becomes a no-op.
                // If stop was requested by the ReadFile then at the end of FireEvent we'll stop the watcher after draining the queue.
                FireEvent();
            }
            
        }
        /// <summary>
        /// Attempts to read the file.
        /// </summary>
        private void ReadFile()
        {
            // Don't reenter, use double check to be on safer side.
            if (isReading) return;
            lock (this)
            {
                if (isReading) return;
                isReading = true;
            }
            // End this session if reached the max retries
            if (retries > maxIdleRetries)
            {
                stopRequested = true;
            } 
            else
            {
                FileInfo file = new FileInfo(fileName);
                if (file.Exists)
                {
                    // we are reading for the first time or the file has been updated
                    if ((LinesCounter == 0) || ((savedLastWriteUtc < file.LastWriteTimeUtc) && (savedLastFileSize < file.Length)))
                    {
                        try
                        {
                            using (FileStream f = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                // okay clear retries
                                retries = 0;

                                // move to the position to saved last
                                f.Position = lastPosition;

                                // actually read the file
                                List<string> lines = new List<string>();
                                using (StreamReader br = new StreamReader(f))
                                {
                                    string line;
                                    while (null != (line = br.ReadLine()))
                                    {
                                        if (canStartAdding)
                                        {
                                            // check if this line matches stopping criteria
                                            if (null != stoppingCriteria)
                                            {
                                                if (stoppingCriteria(line))
                                                {
                                                    stopRequested = true;
                                                    break;
                                                }
                                            }
                                            
                                            // by default any line can be added
                                            bool canAdd = true;
                                            if (null != filteringCriteria)
                                            {
                                                try
                                                {
                                                    canAdd = filteringCriteria(line);
                                                }
                                                catch (Exception)
                                                {
                                                    canAdd = false;
                                                }
                                            }
                                            if (canAdd)
                                            {
                                                lines.Add(line);
                                                LinesCounter++;
                                            }
                                            
                                        }
                                        else
                                        {
                                            // can not add this line yet, so see if it matches the start trigger
                                            if (null != startingCriteria)
                                            {
                                                if (startingCriteria(line))
                                                {
                                                    canStartAdding = true;
                                                }
                                            }
                                            else
                                            {
                                                canStartAdding = true;
                                            }
                                        }
                                    } //end loop

                                    // Save the last pointer , write times etc.
                                    lastPosition = f.Position;
                                    savedLastWriteUtc = file.LastWriteTimeUtc;
                                    lastReadUtc = DateTime.UtcNow;
                                    savedLastFileSize = file.Length;
                                }
                                // if new lines are available queue the events, so other threads can take care of it
                                if (lines.Count > 0)
                                {
                                    lock (this)
                                    {
                                        batchesOflines.Enqueue(lines);
                                    }
                                }
                            }
                        }
                        catch (Exception )
                        { 
                        }
                    }
                    else
                    {
                        retries++; // file can remain "not modified" only so many times or else we should say session is done.
                        if (retries % minIdleRetries == 0) CheckTerminalCondition();
                    }
                }
                else
                {
                    retries++; // file can "not exist" only so many times or else we should say session is done.
                    if (retries % minIdleRetries == 0) CheckTerminalCondition();
                }
            }
            lock (this)
            {
                isReading = false;
            }
            // else wait
            GC.KeepAlive(timer);    
        }

        private void CheckTerminalCondition()
        {
            if (null != terminationCheck)
            {
                try
                {
                    if (terminationCheck())
                    {
                        stopRequested = true;
                    }
                }
                catch (Exception )
                { 

                }
            }
        }
        /// <summary>
        /// Checks if given line satisfies the condition specified in the criteria
        /// </summary>
        /// <param name="line"></param>
        /// <param name="criterion"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        bool CheckCriterion(string line, Func<string, bool> criterion, bool defaultValue)
        {
            bool retValue = defaultValue;
            if (null != criterion)
            { 
                try
                {
                    retValue = criterion(line);
                }
                catch(Exception)
                {
                    retValue = false;
                }
            }
            return retValue;
        }
        /// <summary>
        /// Fire the events
        /// </summary>
        private void FireEvent()
        {
            // guard against reentry
            if(isProcessing) return;
            lock(this)
            {
                if (isProcessing) return;
                isProcessing = true;
            }
            // one by one deque events and fire
            while(batchesOflines.Count>0)
            {
                bool canDeque = (batchsize == -1) ? true : false;
                //List<string> lines = batchesOflines.Dequeue();
                List<string> lines = batchesOflines.Peek();
                var subscribers = Subscription.GetSubscribers(this);
                if (null != subscribers)
                {
                    foreach (IObserver<List<string>> subscriber in subscribers.Cast<IObserver<List<string>>>())
                    {
                        try
                        {
                            subscriber.OnNext(lines);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                if (canDeque)
                {
                    batchesOflines.Dequeue();
                }
                else
                {
                    lines.RemoveRange(0, batchsize);
                }
            }
            // Finally if there is stop requested then stop
            lock (this)
            {
                if (!Done && stopRequested)
                {
                    Stop();
                    Done = true;
                }
                isProcessing = false;
            }
        }
    }
    /// <summary>
    /// Represents a simple string => object dictionary that works as a property bag.
    /// Generally used to parse the line , split it into fields.
    /// </summary>
    public class Record : Dictionary<string, object>
    { 
    }

    public class RecordAnalyzer
    {
        private List<double> values;
        bool timeSpan;

        public RecordAnalyzer(List<Record> records, string key)
        {
            values = Convert(records, key);
        }

        public List<double> Convert(List<Record> records, string key)
        {
            List<double> converted = new List<double>();
            foreach (Record record in records)
            {
                if (record.ContainsKey(key))
                {
                    object value = record[key];
                    double val = 0;
                    if (value is double)
                    {
                        val = (double)value;
                    }
                    else if (value is TimeSpan)
                    {
                        TimeSpan t = (TimeSpan)value;
                        val = t.Ticks;
                        timeSpan = true;
                    }
                    else if (value is string)
                    {
                        val = double.Parse(value as string);
                    }
                    else
                        continue;
                    converted.Add(val);
                }
            }
            return converted;
        }

        private object ConvertResult(double result)
        {
            if (timeSpan) return TimeSpan.FromTicks((long)result);
            return result;
        }

        private double Average()
        {
            if (!values.Any()) return double.NaN;

            double sum = 0;
            int count = 0;
            foreach (double value in values)
            {
                sum += value;
                count++;
            }
            double result = sum / (double)count;
            return result;
        }

        public object GetAverage()
        {
            return ConvertResult(Average());
        }

        public object GetMax()
        {
            if (!values.Any()) return double.NaN;

            double max = double.MinValue;
            foreach (double value in values)
            {
                if (value > max)
                    max = value;
            }
            return ConvertResult(max);
        }

        public object GetMin()
        {
            if (!values.Any()) return double.NaN;

            double min = double.MaxValue;
            foreach (double value in values)
            {
                if (value < min)
                    min = value;
            }
            return ConvertResult(min);
        }

        public object Get1stNorm()
        {
            if (!values.Any()) return double.NaN;

            double average = Average();
            double sum = 0;
            int count = 0;
            foreach (double value in values)
            {
                double diff = Math.Abs(value - average);
                sum += diff;
                count++;
            }
            double result = sum / (double)count;
            return ConvertResult(result);
        }

        public object GetStd()
        {
            if (!values.Any()) return double.NaN;

            double average = Average();
            double sum = 0;
            int count = 0;
            foreach (double value in values)
            {
                double diff = Math.Pow(value - average, 2);
                sum += diff;
                count++;
            }
            double result = Math.Sqrt(sum / (double)count);
            return ConvertResult(result);
        }

        public object GetSum()
        {
            if (!values.Any()) return 0.0;

            double sum = 0;
            foreach (double value in values)
            {
                sum += value;
            }
            return ConvertResult(sum);
        }
    }

    /// <summary>
    /// Treats the file as a stream of records of comma separated values.
    /// </summary>
    public class RecordObserver : BaseFilter<List<string>, List<Record>>
    {
        public string Source { get; private set; }
        public RecordObserver(string source)
        {
            Source = source;
        }
        
        Func<Record, Record> projection;
        protected override List<Record> Transform (List<string> value)
        {
            Assert.IsNotNull(value);
            List < Record > retValue = new List<Record>();
            foreach (string s in value)
            {
                Record record = new Record();
                record["source"] = Source;
                foreach (string part in s.Split(','))
                {
                    string[] subparts = part.Trim().Split(':');
                    if(subparts.Length>1)
                    {
                        record[subparts[0]]=subparts[1];
                    }
                    else
                    {
                        record[subparts[0]]=subparts[0];
                    }
                }
                if (null != projection)
                {
                    record = projection(record);
                }
                retValue.Add(record);
            }
            return retValue;
        }
        public RecordObserver Select(Func<Record,Record> projection)
        {
            Assert.IsNotNull(projection);
            this.projection = projection;
            return this;
        }
    }
    /// <summary>
    /// Extension methods / Syntax helpers
    /// </summary>
    public static class FileObserverExtensions
    {
        // Makes the underlying object into RecordObserver
        static public RecordObserver AsCsv(this FileObserver fileobserver)
        {
            Assert.IsNotNull(fileobserver);
            Assert.IsNotNull(fileobserver.Source);
            RecordObserver ret = new RecordObserver(fileobserver.Source);
            fileobserver.Subscribe(ret);
            return ret;
        }
        /// <summary>
        /// Helper
        /// </summary>
        /// <param name="owner"></param>
        /// <param name="onNextHandler"></param>
        /// <param name="onCompletedHandler"></param>
        /// <param name="onErrorHandler"></param>
        /// <returns></returns>
        static public IDisposable Subscribe(this IObservable<List<string>> owner ,Action<List<string>> onNextHandler, Action onCompletedHandler = null, Action<Exception> onErrorHandler = null)
        {
            Assert.IsNotNull(owner);
            Assert.IsNotNull(onNextHandler);
            IObserver<List<string>> observer = new LambdaWrapperObserver<List<string>>(onNextHandler, onCompletedHandler, onErrorHandler);
            return owner.Subscribe(observer);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="fileobserver1"></param>
        /// <param name="fileobserver2"></param>
        /// <returns></returns>
        static public GroupObserver<List<Record>> Join(this RecordObserver fileobserver1, RecordObserver fileobserver2)
        {
            Assert.IsNotNull(fileobserver1);
            Assert.IsNotNull(fileobserver2);
            return new GroupObserver<List<Record>>(fileobserver1,fileobserver2);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="fileobservergroup"></param>
        /// <param name="fileobserver2"></param>
        /// <returns></returns>
        static public GroupObserver<List<Record>> Join(this GroupObserver<List<Record>> fileobservergroup, RecordObserver fileobserver2)
        {
            Assert.IsNotNull(fileobservergroup);
            Assert.IsNotNull(fileobserver2);
            fileobservergroup.Join(fileobserver2);
            return fileobservergroup;
        }
    }
}
