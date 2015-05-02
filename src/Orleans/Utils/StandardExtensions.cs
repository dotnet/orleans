/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;


namespace Orleans.Runtime
{
    /// <summary>
    /// The Utils class contains a variety of utility methods for use in application and grain code.
    /// </summary>
    internal static class StandardExtensions
    {
        public static TimeSpan Multiply(this TimeSpan timeSpan, double value)
        {
            double ticksD = checked((double)timeSpan.Ticks * value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }

        public static TimeSpan Divide(this TimeSpan timeSpan, double value)
        {
            double ticksD = checked((double)timeSpan.Ticks / value);
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }

        public static double Divide(this TimeSpan first, TimeSpan second)
        {
            double ticks1 = (double)first.Ticks;
            double ticks2 = (double)second.Ticks;
            return ticks1 / ticks2;
        }

        public static TimeSpan Max(TimeSpan first, TimeSpan second)
        {
            return first >= second ? first : second;
        }

        public static TimeSpan Min(TimeSpan first, TimeSpan second)
        {
            return first < second ? first : second;
        }

        public static TimeSpan NextTimeSpan(this SafeRandom random, TimeSpan timeSpan)
        {
            if (timeSpan <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("timeSpan", timeSpan, "SafeRandom.NextTimeSpan timeSpan must be a positive number.");
            double ticksD = ((double)timeSpan.Ticks) * random.NextDouble();
            long ticks = checked((long)ticksD);
            return TimeSpan.FromTicks(ticks);
        }

        public static TimeSpan NextTimeSpan(this SafeRandom random, TimeSpan minValue, TimeSpan maxValue)
        {
            if (minValue <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("minValue", minValue, "SafeRandom.NextTimeSpan minValue must be a positive number.");
            if (minValue >= maxValue) throw new ArgumentOutOfRangeException("minValue", minValue, "SafeRandom.NextTimeSpan minValue must be greater than maxValue.");
            var span = maxValue - minValue;
            return minValue + random.NextTimeSpan(span);
        }
    }
}
