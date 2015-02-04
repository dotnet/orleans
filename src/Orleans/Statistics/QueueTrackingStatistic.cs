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

﻿using System;


namespace Orleans.Runtime
{
    internal class QueueTrackingStatistic
    {
        private readonly AverageValueStatistic averageQueueSizeCounter;
        private readonly CounterStatistic numEnqueuedRequestsCounter;
        private readonly ITimeInterval totalExecutionTime;                  // total time this time is being tracked
        private readonly FloatValueStatistic averageArrivalRate;
        private readonly AverageValueStatistic averageTimeInQueue;
        private static AverageValueStatistic averageTimeInAllQueues;
        private static CounterStatistic totalTimeInAllQueues;

        private static readonly bool TrackExtraStats = false;

        public QueueTrackingStatistic(string queueName)
        {
            if (StatisticsCollector.CollectQueueStats)
            {
                const CounterStorage storage = CounterStorage.LogAndTable;
                averageQueueSizeCounter = AverageValueStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.QUEUES_QUEUE_SIZE_AVERAGE_PER_QUEUE, queueName), storage);
                numEnqueuedRequestsCounter = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.QUEUES_ENQUEUED_PER_QUEUE, queueName), false, storage);

                if (TrackExtraStats)
                {
                    totalExecutionTime = TimeIntervalFactory.CreateTimeInterval(true);
                    averageArrivalRate = FloatValueStatistic.FindOrCreate(
                        new StatisticName(StatisticNames.QUEUES_AVERAGE_ARRIVAL_RATE_PER_QUEUE, queueName),
                       () =>
                       {
                           TimeSpan totalTime = totalExecutionTime.Elapsed;
                           if (totalTime.Ticks == 0) return 0;
                           long numReqs = numEnqueuedRequestsCounter.GetCurrentValue();
                           return (float)((((double)numReqs * (double)TimeSpan.TicksPerSecond)) / (double)totalTime.Ticks);
                       }, storage);
                }

                averageTimeInQueue = AverageValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_TIME_IN_QUEUE_AVERAGE_MILLIS_PER_QUEUE, queueName), storage);
                averageTimeInQueue.AddValueConverter(Utils.AverageTicksToMilliSeconds);

                if (averageTimeInAllQueues == null)
                {
                    averageTimeInAllQueues = AverageValueStatistic.FindOrCreate(new StatisticName(StatisticNames.QUEUES_TIME_IN_QUEUE_AVERAGE_MILLIS_PER_QUEUE, "AllQueues"), storage);
                    averageTimeInAllQueues.AddValueConverter(Utils.AverageTicksToMilliSeconds);
                }
                if (totalTimeInAllQueues == null)
                {
                    totalTimeInAllQueues = CounterStatistic.FindOrCreate(
                        new StatisticName(StatisticNames.QUEUES_TIME_IN_QUEUE_TOTAL_MILLIS_PER_QUEUE, "AllQueues"), false, storage);
                    totalTimeInAllQueues.AddValueConverter(Utils.TicksToMilliSeconds);
                }
            } 
        }

        public void OnEnQueueRequest(int numEnqueuedRequests, int queueLength)
        {
            numEnqueuedRequestsCounter.IncrementBy(numEnqueuedRequests);
            averageQueueSizeCounter.AddValue(queueLength);
        }

        public void OnEnQueueRequest(int numEnqueuedRequests, int queueLength, ITimeInterval itemInQueue)
        {
            numEnqueuedRequestsCounter.IncrementBy(numEnqueuedRequests);
            averageQueueSizeCounter.AddValue(queueLength);
            itemInQueue.Start();
        }

        public void OnDeQueueRequest(ITimeInterval itemInQueue)
        {
            itemInQueue.Stop();
            long ticks = itemInQueue.Elapsed.Ticks;
            averageTimeInQueue.AddValue(ticks);
            averageTimeInAllQueues.AddValue(ticks);
            totalTimeInAllQueues.IncrementBy(ticks);
        }

        public float AverageQueueLength { get { return averageQueueSizeCounter.GetAverageValue(); } }
        public long NumEnqueuedRequests { get { return numEnqueuedRequestsCounter.GetCurrentValue(); } }
        public float ArrivalRate { get { return averageArrivalRate == null ? 0 : averageArrivalRate.GetCurrentValue(); } }

        public void OnStartExecution()
        {
            if (TrackExtraStats)
            {
                totalExecutionTime.Start();
            }
        }

        public void OnStopExecution()
        {
            if (TrackExtraStats)
            {
                totalExecutionTime.Stop();
            }
        }
    }
}
