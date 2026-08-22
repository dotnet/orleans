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

namespace Orleans.AdvancedReminders.Cron.Internal;

internal static class CalendarHelper
{
    private const int DaysPerWeekCount = 7;

    public static bool IsGreaterThan(int year1, int month1, int day1, int year2, int month2, int day2)
    {
        if (year1 != year2) return year1 > year2;
        if (month1 != month2) return month1 > month2;
        return day1 > day2;
    }

    public static long DateTimeToTicks(int year, int month, int day, int hour, int minute, int second)
    {
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks;
    }

    public static void FillDateTimeParts(
        long ticks,
        out int second,
        out int minute,
        out int hour,
        out int day,
        out int month,
        out int year)
    {
        var value = new DateTime(ticks, DateTimeKind.Utc);

        second = value.Second;
        if (ticks % TimeSpan.TicksPerSecond != 0)
        {
            // Preserve scheduler semantics: non-round timestamps move to the next second.
            second++;
        }

        minute = value.Minute;
        hour = value.Hour;
        (year, month, day) = value;
    }

    public static DayOfWeek GetDayOfWeek(int year, int month, int day)
    {
        return new DateTime(year, month, day).DayOfWeek;
    }

    public static int GetDaysInMonth(int year, int month)
    {
        return DateTime.DaysInMonth(year, month);
    }

    public static int MoveToNearestWeekDay(int year, int month, int day)
    {
        var dayOfWeek = GetDayOfWeek(year, month, day);
        if (dayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
        {
            return day;
        }

        if (dayOfWeek == DayOfWeek.Sunday)
        {
            return day == GetDaysInMonth(year, month) ? day - 2 : day + 1;
        }

        return day == CronField.DaysOfMonth.First ? day + 2 : day - 1;
    }

    public static bool IsNthDayOfWeek(int day, int n)
    {
        return day - DaysPerWeekCount * n < CronField.DaysOfMonth.First
               && day - DaysPerWeekCount * (n - 1) >= CronField.DaysOfMonth.First;
    }

    public static bool IsLastDayOfWeek(int year, int month, int day)
    {
        return day + DaysPerWeekCount > GetDaysInMonth(year, month);
    }
}
