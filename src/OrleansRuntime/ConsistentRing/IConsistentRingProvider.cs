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

﻿
namespace Orleans.Runtime.ConsistentRing
{
    // someday, this will be the only provider for the ring, i.e., directory service will use this

    internal interface IConsistentRingProvider
    {
        /// <summary>
        /// Get the responsbility range of the current silo
        /// </summary>
        /// <returns></returns>
        IRingRange GetMyRange();

        // the following two are similar to the ISiloStatusOracle interface ... this replaces the 'OnRangeChanged' because OnRangeChanged only supports one subscription

        /// <summary>
        /// Subscribe to receive range change notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive range change notifications.</param>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        bool SubscribeToRangeChangeEvents(IRingRangeListener observer);

        /// <summary>
        /// Unsubscribe from receiving range change notifications
        /// </summary>
        /// <param name="observer">An observer interface to receive range change notifications.</param>
        /// <returns>bool value indicating that unsubscription succeeded or not</returns>
        bool UnSubscribeFromRangeChangeEvents(IRingRangeListener observer);

        /// <summary>
        /// Get the silo responsible for <paramref name="key"/> according to consistent hashing
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        SiloAddress GetPrimaryTargetSilo(uint key);
    }

    // similar to ISiloStatusListener
    internal interface IRingRangeListener
    {
        void RangeChangeNotification(IRingRange old, IRingRange now, bool increased);
    }
}
