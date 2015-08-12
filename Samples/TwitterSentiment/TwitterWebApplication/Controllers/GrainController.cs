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

using System.Threading.Tasks;
using System.Web.Mvc;
using Orleans;
using TwitterGrainInterfaces;

namespace TwitterWebApplication.Controllers
{
    public class GrainController : Controller
    {
        /// <summary>
        /// Return the main view
        /// </summary>
        /// <returns></returns>
        public ActionResult Index()
        {
            return View();
        }

        /// <summary>
        /// Set the score for a set of comma separated hashtags
        /// </summary>
        /// <param name="hashtags"></param>
        /// <param name="score"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult> SetScore(string hashtags, int score)
        {
            // read the body of the tweet
            var tweet = await ReadInputStreamAsync();

            // get a handle the to dispatcher grain
            var grain = GrainClient.GrainFactory.GetGrain<ITweetDispatcherGrain>(0);

            // set the score for the hashtags
            await grain.AddScore(score, hashtags.ToLower().Split(','), tweet);

            return this.Content("");
        }

        /// <summary>
        /// Get the score for a set of comma separated hashtags
        /// </summary>
        /// <param name="hashtags"></param>
        /// <returns></returns>
        public async Task<ActionResult> GetScores(string hashtags)
        {
            // get a handle the to dispatcher grain
            var tweetGrain = GrainClient.GrainFactory.GetGrain<ITweetDispatcherGrain>(0);

            // get the scores for the hashtags
            var tweetGrainTask = tweetGrain.GetTotals(hashtags.ToLower().Split(','));

            // get a handle the to counter grain
            var counterGrain = GrainClient.GrainFactory.GetGrain<ICounter>(0);

            // get the total number of hashtag activations
            var counterGrainTask = counterGrain.GetTotalCounter();

            // wait for tasks to complete
            await Task.WhenAll(tweetGrainTask, counterGrainTask);

            // return the json
            return Json(new object[] { tweetGrainTask.Result, counterGrainTask.Result }, JsonRequestBehavior.AllowGet);
        }

        /// <summary>
        /// Read the body of a request in UTF8
        /// </summary>
        /// <returns></returns>
        async Task<string> ReadInputStreamAsync()
        {
            var data = new byte[1024];
            var output = "";
            while (true)
            {
                var numBytesRead = await this.Request.InputStream.ReadAsync(data, 0, 1024);
                if (numBytesRead <= 0) return output;
                output += System.Text.Encoding.UTF8.GetString(data, 0, numBytesRead);
            }
        }
    }

}