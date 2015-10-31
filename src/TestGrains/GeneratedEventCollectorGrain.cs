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
using TestGrainInterfaces;

namespace TestGrains
{
    [ImplicitStreamSubscription(StreamNamespace)]
    public class GeneratedEventCollectorGrain : Grain, IGeneratedEventCollectorGrain
    {
        public static Guid ReporterId = new Guid("f83247af-c14d-422c-8141-74d7a79717dc");
        public const string StreamNamespace = "Generated";
        public const string StreamProviderName = "GeneratedStreamProvider";

        private Logger logger;
        private IAsyncStream<GeneratedEvent> stream;
        private int counter;

        public override async Task OnActivateAsync()
        {
            logger = base.GetLogger("GeneratedEvenCollectorGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");

            var streamProvider = GetStreamProvider(StreamProviderName);
            stream = streamProvider.GetStream<GeneratedEvent>(this.GetPrimaryKey(), StreamNamespace);

            await stream.SubscribeAsync(
                (e, t) =>
                {
                    counter++;
                    logger.Info("Received a generated event {0}, of {1} events", e, counter);
                    if (e.EventType == GeneratedEvent.GeneratedEventType.Fill)
                    {
                        return TaskDone.Done;
                    }
                    var reporter = this.GrainFactory.GetGrain<IGeneratedEventReporterGrain>(ReporterId);
                    return reporter.ReportResult(this.GetPrimaryKey(), StreamProviderName, StreamNamespace, counter);
                });
        }
    }
}
