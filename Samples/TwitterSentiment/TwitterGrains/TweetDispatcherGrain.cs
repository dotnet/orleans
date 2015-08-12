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

using Orleans.Concurrency;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TwitterGrainInterfaces;

namespace Grains
{
    /// <summary>
    /// This grain acts as the API into the hashtag grain, and allows you to set the score of multiple hashtags at once
    /// </summary>
    [StatelessWorker]
    public class TweetDispatcherGrain : Orleans.Grain, ITweetDispatcherGrain
    {
        /// <summary>
        /// disptach each hashtag to the appropriate grain, using the hashtag as the grain key
        /// </summary>
        /// <param name="score"></param>
        /// <param name="hashtags"></param>
        /// <param name="tweet"></param>
        /// <returns></returns>
        public async Task AddScore(int score, string[] hashtags, string tweet)
        {
            // fan out to the grains for all hashtags to set their scores
            var tasks = new List<Task>();
            foreach (var hashtag in hashtags)
            {
                var grain = GrainFactory.GetGrain<IHashtagGrain>(0, hashtag, null);
                var task = grain.AddScore(score, tweet);
                tasks.Add(task);
            }
            await Task.WhenAll(tasks);
        }


        /// <summary>
        /// retrieve the totals for a set of hashtags
        /// </summary>
        /// <param name="hashtags"></param>
        /// <returns></returns>
        public async Task<Totals[]> GetTotals(string[] hashtags)
        {
            // fan out to the grains for all hashtags to retrieve their scores
            var tasks = new List<Task<Totals>>();
            foreach (var hashtag in hashtags)
            {
                var grain = GrainFactory.GetGrain<IHashtagGrain>(0, hashtag, null);
                tasks.Add(grain.GetTotals());

            }
            await Task.WhenAll(tasks);
            return tasks.Select(x => x.Result).ToArray();
        }
    }
}
