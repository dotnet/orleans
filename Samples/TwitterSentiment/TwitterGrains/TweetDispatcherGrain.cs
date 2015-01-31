//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

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
                var grain = HashtagGrainFactory.GetGrain(0, hashtag);
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
                var grain = HashtagGrainFactory.GetGrain(0, hashtag);
                tasks.Add(grain.GetTotals());

            }
            await Task.WhenAll(tasks);
            return tasks.Select(x => x.Result).ToArray();
        }
    }
}
