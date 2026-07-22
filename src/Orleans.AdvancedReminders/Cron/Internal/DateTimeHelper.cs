// The MIT License (MIT)
//
// Copyright (c) 2017 Hangfire OÜ
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// Derived from Cronos: https://github.com/HangfireIO/Cronos
#nullable enable
using System;

namespace Orleans.AdvancedReminders.Cron.Internal
{
    internal static class DateTimeHelper
    {
        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

        public static DateTime FloorToSeconds(DateTime dateTime)
        {
            return dateTime.AddTicks(-GetExtraTicks(dateTime.Ticks));
        }

        public static bool IsRound(DateTime dateTime)
        {
            return GetExtraTicks(dateTime.Ticks) == 0;
        }

        private static long GetExtraTicks(long ticks)
        {
            return ticks % OneSecond.Ticks;
        }
    }
}
