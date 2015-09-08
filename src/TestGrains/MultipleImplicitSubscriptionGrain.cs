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
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{

    [ImplicitStreamSubscription("red")]
    [ImplicitStreamSubscription("blue")]
    public class MultipleImplicitSubscriptionGrain : Grain, IMultipleImplicitSubscriptionGrain
    {
        private Logger logger;
        private IAsyncStream<int> redStream, blueStream;
        private int redCounter, blueCounter;

        public override async Task OnActivateAsync()
        {
            logger = base.GetLogger("MultipleImplicitSubscriptionGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");

            var streamProvider = GetStreamProvider("SMSProvider");
            redStream = streamProvider.GetStream<int>(this.GetPrimaryKey(), "red");
            blueStream = streamProvider.GetStream<int>(this.GetPrimaryKey(), "blue");

            await redStream.SubscribeAsync(
                (e, t) =>
                {
                    logger.Info("Received a red event {0}", e);
                    redCounter++;
                    return TaskDone.Done;
                });

            await blueStream.SubscribeAsync(
                (e, t) =>
                {
                    logger.Info("Received a blue event {0}", e);
                    blueCounter++;
                    return TaskDone.Done;
                });
        }

        public Task<Tuple<int, int>> GetCounters()
        {
            return Task.FromResult(new Tuple<int, int>(redCounter, blueCounter));
        }
    }
}