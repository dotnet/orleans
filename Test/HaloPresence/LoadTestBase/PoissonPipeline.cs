using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

using Orleans;
using Orleans.Runtime;


namespace LoadTestBase
{
    public class PoissonPipeline : IPipeline
    {
        private Random rand;
        private double reqPerMillisecond;
        private double needToWait;
        private HashSet<Task> running;
        private Stopwatch watch;

        public PoissonPipeline(int reqPerSecond)
        {
            rand = new Random();
            reqPerMillisecond = ((double)reqPerSecond) / 1000;
            needToWait = 0;
            running = new HashSet<Task>();
            watch = new Stopwatch();
        }

        public int Count { get { return 0; } } // We don't care

        public void Add(Task t)
        {
            if (!watch.IsRunning)
            {
                watch.Start();
            }
            lock (this)
            {
                running.RemoveWhere(x => x.IsCanceled || x.IsCompleted || x.IsFaulted);
                while (running.Count > 1000)
                {
                    Thread.Sleep(1);
                    running.RemoveWhere(x => x.IsCanceled || x.IsCompleted || x.IsFaulted);
                    needToWait = watch.ElapsedMilliseconds;
                }
                double val = Exponential(reqPerMillisecond);
                needToWait += val;
                double owedTime = needToWait - watch.ElapsedMilliseconds;
                if (owedTime > 1.0)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(owedTime));
                }
                t.Ignore();
                running.Add(t);
            }
        }

        public void Wait()
        {
            Task.WhenAll(running).Wait();
        }

        private double Exponential(double rate)
        {
            double uniform = rand.NextDouble();
            while (uniform == 1) // just to be safe, can't do Log(0)
            {
                uniform = rand.NextDouble();
            }
            double exponential = Math.Log(1 - uniform) / (-1.0 * rate);
            return exponential;
        }
    }
}