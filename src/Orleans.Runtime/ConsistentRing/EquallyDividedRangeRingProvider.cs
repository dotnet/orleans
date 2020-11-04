using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;

namespace Orleans.Runtime.ConsistentRing
{
    internal sealed class EquallyDividedRangeRingProvider : IConsistentRingProviderForGrains, IRingRangeListener
    {
        private readonly IConsistentRingProvider ringProvider;
        private readonly List<IAsyncRingRangeListener> grainStatusListeners = new List<IAsyncRingRangeListener>();
        private readonly ILogger logger;
        private readonly int numSubRanges;
        private readonly int mySubRangeIndex;
        private IRingRange myRange;

        internal EquallyDividedRangeRingProvider(IConsistentRingProvider provider, ILoggerFactory loggerFactory, int mySubRangeIndex, int numSubRanges)
        {
            if (mySubRangeIndex < 0 || mySubRangeIndex >= numSubRanges)
                throw new IndexOutOfRangeException("mySubRangeIndex is out of the range. mySubRangeIndex = " + mySubRangeIndex + " numSubRanges = " + numSubRanges);

            ringProvider = provider;
            this.numSubRanges = numSubRanges;
            this.mySubRangeIndex = mySubRangeIndex;
            ringProvider.SubscribeToRangeChangeEvents(this);
            logger = loggerFactory.CreateLogger<EquallyDividedRangeRingProvider>();
        }

        public IRingRange GetMyRange() => myRange ??= CalcMyRange();

        private IRingRange CalcMyRange() => RangeFactory.GetEquallyDividedSubRange(ringProvider.GetMyRange(), numSubRanges, mySubRangeIndex);

        public bool SubscribeToRangeChangeEvents(IAsyncRingRangeListener observer)
        {
            lock (grainStatusListeners)
            {
                if (grainStatusListeners.Contains(observer)) return false;

                grainStatusListeners.Add(observer);
                return true;
            }
        }

        public bool UnSubscribeFromRangeChangeEvents(IAsyncRingRangeListener observer)
        {
            lock (grainStatusListeners)
            {
                return grainStatusListeners.Remove(observer);
            }
        }

        public void RangeChangeNotification(IRingRange old, IRingRange now, bool increased)
        {
            myRange = CalcMyRange();

            var oldSubRange = RangeFactory.GetEquallyDividedSubRange(old, numSubRanges, mySubRangeIndex);
            var newSubRange = RangeFactory.GetEquallyDividedSubRange(now, numSubRanges, mySubRangeIndex);

            if (oldSubRange.Equals(newSubRange)) return;

            // For now, always say your range increased and the listeners need to deal with the situation when they get the same range again anyway.
            // In the future, check sub range inclusion. Note that it is NOT correct to just return the passed increased argument. 
            // It will be wrong: the multi range may have decreased, while some individual sub range may partialy increase (shift).

            logger.Info("-NotifyLocal GrainRangeSubscribers about old {0} and new {1} increased? {2}.", oldSubRange.ToString(), newSubRange.ToString(), increased);

            IAsyncRingRangeListener[] copy;
            lock (grainStatusListeners)
            {
                copy = grainStatusListeners.ToArray();
            }
            foreach (IAsyncRingRangeListener listener in copy)
            {
                try
                {
                    Task task = listener.RangeChangeNotification(oldSubRange, newSubRange);
                    // We don't want to await it here since it will delay delivering notifications to other listeners.
                    // We only want to log an error if it happends, so use ContinueWith.
                    task.LogException(logger, ErrorCode.CRP_ForGrains_Local_Subscriber_Exception_1,
                                        String.Format("Local IGrainRingRangeListener {0} has thrown an asynchronous exception when was notified about RangeChangeNotification about old {1} new {2}.",
                                        listener.GetType().FullName, oldSubRange, newSubRange))
                        .Ignore();
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.CRP_ForGrains_Local_Subscriber_Exception_2,
                        String.Format("Local IGrainRingRangeListener {0} has thrown an exception when was notified about RangeChangeNotification about old {1} new {2}.",
                        listener.GetType().FullName, oldSubRange, newSubRange), exc);
                }
            }
        }
    }
}


