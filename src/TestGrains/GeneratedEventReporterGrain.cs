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
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using TestGrainInterfaces;

namespace TestGrains
{
    class GeneratedEventReporterGrain : Grain, IGeneratedEventReporterGrain
    {
        private Logger logger;

        private Dictionary<Tuple<string, string>, Dictionary<Guid, int>> reports;

        public override Task OnActivateAsync()
        {
            logger = base.GetLogger("GeneratedEventReporterGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");

            reports = new Dictionary<Tuple<string, string>, Dictionary<Guid, int>>();
            return base.OnActivateAsync();
        }

        public Task ReportResult(Guid streamGuid, string streamProvider, string streamNamespace, int count)
        {
            Dictionary<Guid, int> counts;
            Tuple<string, string> key = Tuple.Create(streamProvider, streamNamespace);
            if (!reports.TryGetValue(key, out counts))
            {
                counts = new Dictionary<Guid, int>();
                reports.Add(key, counts);
            }
            logger.Info("ReportResult. StreamProvider: {0}, StreamNamespace: {1}, StreamGuid: {2}, Count: {3}", streamProvider, streamNamespace, streamGuid, count);
            counts[streamGuid] = count;
            return TaskDone.Done;
        }

        public Task<IDictionary<Guid, int>> GetReport(string streamProvider, string streamNamespace)
        {
            Dictionary<Guid, int> counts;
            Tuple<string, string> key = Tuple.Create(streamProvider, streamNamespace);
            if (!reports.TryGetValue(key, out counts))
            {
                return Task.FromResult<IDictionary<Guid, int>>(new Dictionary<Guid, int>());
            }
            return Task.FromResult<IDictionary<Guid, int>>(counts);
        }
    }
}
