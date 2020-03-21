using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;


namespace Orleans.Samples.Chirper.Network.Loader
{
    class StatKeeper : IDisposable
    {
        private readonly PerformanceCounter memoryMonitor;
        private readonly PerformanceCounter processorMonitor;

        public DateTime StartTime {get; private set;}
        public DateTime StopTime {get; private set;}

        public const int BytesInAMegabyte = 1048576;


        private readonly Stopwatch stopwatch;

        public List<Tuple<TimeSpan, long, float>> Marks { get; private set; }

        public string ProcessName { get; private set; }

        public StatKeeper()
        {
            ProcessName = Process.GetCurrentProcess().ProcessName;
            this.memoryMonitor = new PerformanceCounter("Process", "Working Set", ProcessName);
            this.processorMonitor = new PerformanceCounter("Process", "% Processor Time", ProcessName);
            this.stopwatch = new Stopwatch();
            this.Marks = new List<Tuple<TimeSpan, long, float>>();
        }

        public void Start()
        {
            this.StartTime = DateTime.Now;
            this.memoryMonitor.NextSample();
            this.processorMonitor.NextValue();
            this.stopwatch.Start();

        }

        public void Stop()
        {
            this.stopwatch.Stop();
            this.Mark();
            this.StopTime = DateTime.Now;
        }

        public void Mark()
        {
            TimeSpan time = this.stopwatch.Elapsed;
            long memory = this.memoryMonitor.NextSample().RawValue;
            float processor = this.processorMonitor.NextValue();

            Tuple<TimeSpan, long, float> mark = new Tuple<TimeSpan, long, float>(time, memory, processor);
            this.Marks.Add(mark);
        }

        public Tuple<TimeSpan, long, float> CalculateAverages()
        {
            double sumMiliseconds = 0;
            TimeSpan previousElapsedTime = new TimeSpan();
            long memorySum = 0;
            float processorSum = 0;
            

            foreach (Tuple<TimeSpan, long, float> mark in Marks)
            {
                sumMiliseconds += (mark.Item1 - previousElapsedTime).TotalMilliseconds;
                previousElapsedTime = mark.Item1;
                memorySum += mark.Item2;
                processorSum += mark.Item3;
            }
            double averageMiliseconds = sumMiliseconds / Marks.Count;
            TimeSpan averageBlockTime = TimeSpan.FromMilliseconds(averageMiliseconds);
            long averageMemory = memorySum / Marks.Count;
            float averageProcessor = processorSum / Marks.Count;

            return new Tuple<TimeSpan, long, float>(averageBlockTime, averageMemory, averageProcessor);
        }

        public string GetSystemStats()
        {
            StringBuilder statMessage = new StringBuilder();
            if (Marks.Count > 0)
            {
                Tuple<TimeSpan, long, float> averages = this.CalculateAverages();
                statMessage.AppendFormat("Average Block Time:    {0}", averages.Item1);
                statMessage.AppendLine();
                statMessage.AppendLine();

                statMessage.AppendFormat("Memory Usage  -   Start: {0,8} MB", Marks[0].Item2 / BytesInAMegabyte);
                statMessage.AppendFormat("    End: {0,8} MB", Marks[Marks.Count - 1].Item2 / BytesInAMegabyte);
                statMessage.AppendFormat("    Avg: {0,8} MB", averages.Item2 / BytesInAMegabyte);
                statMessage.AppendLine();

                statMessage.AppendFormat("Processor Usage - Start: {0,10:F3}%", Marks[0].Item3);
                statMessage.AppendFormat("    End: {0,10:F3}%", Marks[Marks.Count - 1].Item3);
                statMessage.AppendFormat("    Avg: {0,10:F3}%", averages.Item3);
            }
            else
            {
                statMessage.AppendLine("No stats collected.");
            }

            return statMessage.ToString();
        }


        public void Dispose()
        {
            this.memoryMonitor.Close();
            this.processorMonitor.Close();
        }
    }
}
