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

internal static class TimeZoneHelper
{
    // Use platform-native DST ambiguity logic from TimeZoneInfo.
    public static bool IsAmbiguousTime(TimeZoneInfo zone, DateTime ambiguousTime)
    {
        return zone.IsAmbiguousTime(ambiguousTime);
    }

    public static TimeSpan GetDaylightOffset(TimeZoneInfo zone, DateTime ambiguousDateTime)
    {
        var offsets = GetAmbiguousOffsets(zone, ambiguousDateTime);
        var baseOffset = zone.GetUtcOffset(ambiguousDateTime);

        if (offsets[0] != baseOffset) return offsets[0];
        return offsets[1];
    }

    public static DateTimeOffset GetDaylightTimeStart(TimeZoneInfo zone, DateTime invalidDateTime)
    {
        var dstTransitionDateTime = new DateTime(invalidDateTime.Year, invalidDateTime.Month, invalidDateTime.Day,
            invalidDateTime.Hour, invalidDateTime.Minute, 0, 0, invalidDateTime.Kind);

        while (zone.IsInvalidTime(dstTransitionDateTime))
        {
            dstTransitionDateTime = dstTransitionDateTime.AddMinutes(1);
        }

        var dstOffset = zone.GetUtcOffset(dstTransitionDateTime);

        return new DateTimeOffset(dstTransitionDateTime, dstOffset);
    }

    public static DateTimeOffset GetStandardTimeStart(TimeZoneInfo zone, DateTime ambiguousTime, TimeSpan daylightOffset)
    {
        var dstTransitionEnd = GetDstTransitionEndDateTime(zone, ambiguousTime);
        var baseOffset = zone.GetUtcOffset(ambiguousTime);

        return new DateTimeOffset(dstTransitionEnd, daylightOffset).ToOffset(baseOffset);
    }

    public static DateTimeOffset GetAmbiguousIntervalEnd(TimeZoneInfo zone, DateTime ambiguousTime)
    {
        var dstTransitionEnd = GetDstTransitionEndDateTime(zone, ambiguousTime);
        var baseOffset = zone.GetUtcOffset(ambiguousTime);

        return new DateTimeOffset(dstTransitionEnd, baseOffset);
    }

    public static DateTimeOffset GetDaylightTimeEnd(TimeZoneInfo zone, DateTime ambiguousTime, TimeSpan daylightOffset)
    {
        var daylightTransitionEnd = GetDstTransitionEndDateTime(zone, ambiguousTime);

        return new DateTimeOffset(daylightTransitionEnd.AddTicks(-1), daylightOffset);
    }

    private static TimeSpan[] GetAmbiguousOffsets(TimeZoneInfo zone, DateTime ambiguousTime)
    {
        return zone.GetAmbiguousTimeOffsets(ambiguousTime);
    }

    private static DateTime GetDstTransitionEndDateTime(TimeZoneInfo zone, DateTime ambiguousDateTime)
    {
        var dstTransitionDateTime = new DateTime(ambiguousDateTime.Year, ambiguousDateTime.Month, ambiguousDateTime.Day,
            ambiguousDateTime.Hour, ambiguousDateTime.Minute, 0, 0, ambiguousDateTime.Kind);

        while (zone.IsAmbiguousTime(dstTransitionDateTime))
        {
            dstTransitionDateTime = dstTransitionDateTime.AddMinutes(1);
        }

        return dstTransitionDateTime;
    }
}
