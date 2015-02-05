using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace ResultsInterpretor
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: <prog> [input file]");
                Environment.Exit(1);
            }

            string line;
            List<DateTime> times = new List<DateTime>();

            System.IO.StreamReader file =
               new System.IO.StreamReader(args[0]);
            while ((line = file.ReadLine()) != null)
            {
                string[] timeParts = line.Split(':');
                times.Add(new DateTime(1, 1, 1, Int32.Parse(timeParts[0]), Int32.Parse(timeParts[1]), Int32.Parse(timeParts[2])));
            }
            file.Close();

            times.Sort();
            DateTime changeStarted = times[0];
            Dictionary<int, int> mapCountToTimes = new Dictionary<int, int>();

            for (int i = 1; i < times.Count; i++)
            {
                int delta = (int)times[i].Subtract(changeStarted).TotalSeconds;
                if (!mapCountToTimes.ContainsKey(delta))
                {
                    mapCountToTimes[delta] = 0;
                }
                mapCountToTimes[delta]++;
            }

            List<int> deltas = mapCountToTimes.Keys.ToList();
            deltas.Sort();

            int time = 0;
            int numSilos = 0;
            for (int i = 0; i < deltas.Count; i++)
            {
                int nextDelta = deltas[i];
                while (time < nextDelta)
                {
                    Console.WriteLine("{0} {1}", time, numSilos);
                    time++;
                }
                numSilos += mapCountToTimes[nextDelta];
                Console.WriteLine("{0} {1}", nextDelta, numSilos);
                time = nextDelta + 1;
            }
        }
    }
}
