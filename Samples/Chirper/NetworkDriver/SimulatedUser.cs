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

using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Samples.Chirper.GrainInterfaces;

namespace Orleans.Samples.Chirper.Network.Driver
{
    class SimulatedUser : IDisposable
    {
        public double ShouldRechirpRate { get; set; }
        public int ChirpPublishTimebase { get; set; }
        public bool ChirpPublishTimeRandom { get; set; }
        public bool Verbose { get; set; }

        readonly IChirperAccount user;
        readonly Task<long> getUserIdAsync;
        long userId;

        public SimulatedUser(IChirperAccount user)
        {
            this.user = user;
            this.getUserIdAsync = user.GetUserId();
        }

        public async void Start()
        {
            this.userId = await getUserIdAsync;
            Console.WriteLine("Starting simulating Chirper user id=" + userId);
        }

        public void Stop()
        {
            Console.WriteLine("Stopping simulating Chirper user id=" + userId);
        }

        #region IDisposable interface

        public void Dispose()
        {
            Stop();
        }
        #endregion

        public Task PublishMessage(string message)
        {
            return user.PublishMessage(message);
        }
    }
}
