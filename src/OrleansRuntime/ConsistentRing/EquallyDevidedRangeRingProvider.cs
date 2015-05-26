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
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;


namespace Orleans.Runtime.ConsistentRing
{
    internal class EquallyDevidedRangeRingProvider : IConsistentRingProviderForGrains, IRingRangeListener
    {
        private readonly IConsistentRingProvider ringProvider;
        private readonly List<IGrainRingRangeListener> grainStatusListeners;
        private readonly TraceLogger logger;
        private readonly int numSubRanges;
        private readonly int mySubRangeIndex;
        private IRingRange myRange;

        internal EquallyDevidedRangeRingProvider(IConsistentRingProvider provider, int mySubRangeIndex, int numSubRanges)
        {
            if (mySubRangeIndex < 0 || mySubRangeIndex >= numSubRanges)
                throw new IndexOutOfRangeException("mySubRangeIndex is out of the range. mySubRangeIndex = " + mySubRangeIndex + " numSubRanges = " + numSubRanges);
            
            ringProvider = provider;
            this.numSubRanges = numSubRanges;
            this.mySubRangeIndex = mySubRangeIndex;
            grainStatusListeners = new List<IGrainRingRangeListener>();
            ringProvider.SubscribeToRangeChangeEvents(this);
            logger = TraceLogger.GetLogger(typeof(EquallyDevidedRangeRingProvider).Name);
        }

        public IRingRange GetMyRange()
        {
            return myRange ?? (myRange = CalcMyRange());
        }

        private IRingRange CalcMyRange()
        {
            var equallyDevidedMultiRange = new EquallyDevidedMultiRange(ringProvider.GetMyRange(), numSubRanges);
            return equallyDevidedMultiRange.GetSubRange(mySubRangeIndex);
        }

        public bool SubscribeToRangeChangeEvents(IGrainRingRangeListener observer)
        {
            lock (grainStatusListeners)
            {
                if (grainStatusListeners.Contains(observer)) return false;

                grainStatusListeners.Add(observer);
                return true;
            }
        }

        public bool UnSubscribeFromRangeChangeEvents(IGrainRingRangeListener observer)
        {
            lock (grainStatusListeners)
            {
                return grainStatusListeners.Contains(observer) && grainStatusListeners.Remove(observer);
            }
        }

        public void RangeChangeNotification(IRingRange old, IRingRange now, bool increased)
        {
            myRange = CalcMyRange();

            var oldMultiRange = new EquallyDevidedMultiRange(old, numSubRanges);
            IRingRange oldSubRange = oldMultiRange.GetSubRange(mySubRangeIndex);
            var newMultiRange = new EquallyDevidedMultiRange(now, numSubRanges);
            IRingRange newSubRange = newMultiRange.GetSubRange(mySubRangeIndex);

            if (oldSubRange.Equals(newSubRange)) return;

            // For now, always say your range increased and the listeners need to deal with the situation when they get the same range again anyway.
            // In the future, check sub range inclusion. Note that it is NOT correct to just return the passed increased argument. 
            // It will be wrong: the multi range may have decreased, while some individual sub range may partialy increase (shift).

            logger.Info("-NotifyLocal GrainRangeSubscribers about old {0} and new {1} increased? {2}.", oldSubRange.ToString(), newSubRange.ToString(), increased);

            List<IGrainRingRangeListener> copy;
            lock (grainStatusListeners)
            {
                copy = grainStatusListeners.ToList();
            }
            foreach (IGrainRingRangeListener listener in copy)
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


