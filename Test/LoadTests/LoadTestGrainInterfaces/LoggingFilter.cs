using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LoadTestGrainInterfaces
{
    public class LoggingFilter
    {
        public double Verbosity { get; private set; }
        private readonly ulong _period;
        public long Period { get { return (long)_period; } }
        private ulong _counter = 0;
        private readonly Action<string> _logFunc;

        public LoggingFilter(double verbosity, long period, Action<string> logFunc)
        {
            if (verbosity < 0 || verbosity > 1.0)
                throw new ArgumentOutOfRangeException("verbosity", verbosity, "chance must be on [0, 1.0]");
            if (period < 0)
                throw new ArgumentOutOfRangeException("period", period, "period cannot be less than 0.");
            Verbosity = verbosity;
            _period = (ulong)period;
            _logFunc = logFunc;

            if (verbosity > 0 && logFunc != null)
                _logFunc(string.Format("LoggingFilter created; verbosity={0}, period={1}", verbosity, period));
        }

        public bool ShouldLog(ulong counter)
        {
            return (Verbosity > 0 && (counter % (ulong)(Verbosity * _period)) == 0);
        }

        public bool ShouldLog()
        {
            bool result = ShouldLog(_counter);
            IncrementCounter(ref _counter);
            return result;
        }

        public static void IncrementCounter(ref ulong counter)
        {
            // we increment _counter as an unchecked operation because we want it to wrap.
            unchecked
            {
                counter++;
            }
        }

        public LoggingFilter Clone()
        {
            return new LoggingFilter(Verbosity, Period, _logFunc);
        }
    }
}
