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

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Samples.Chirper.GrainInterfaces
{
    /// <summary>
    /// Orleans grain interface IChirperPublisher
    /// </summary>
    public interface IChirperPublisher : IGrain
    {
        /// <summary>Unique Id for this actor / user</summary>
        Task<long> GetUserId();

        /// <summary>Alias / username for this actor / user</summary>
        Task<string> GetUserAlias();

        /// <summary>Request a copy of the most recent 'n' Chirp messages posted by this publisher, from the specified start position.</summary>
        /// <param name="n">Number of Chirp messages requested. A value of -1 means all messages.</param>
        /// <param name="start">The start position for returned messages. A value of 0 means start with most recent message. A positive value means skip past that many of the most recent messages</param>
        /// <returns>Bulk list of Chirp messages posted by this publisher</returns>
        /// <remarks>The publisher might only return a partial record of historic events due to message retention policies.</remarks>
        Task<List<ChirperMessage>> GetPublishedMessages(int n = 10, int start = 0);

        /// <summary>Subscribe from receiving notifications of new Chirps sent by this publisher</summary>
        /// <param name="userAlias">The alias of the new subscriber now following this user</param>
        /// <param name="userId">The id of the new subscriber</param>
        /// <param name="follower">The new subscriber now following this user</param>
        /// <returns>AsyncCompletion status for this operation</returns>
        Task AddFollower(string userAlias, long userId, IChirperSubscriber follower);

        /// <summary>Unsubscribe from receiving notifications of new Chirps sent by this publisher</summary>
        /// <param name="userAlias">The alias of the subscriber to be removed</param>
        /// <param name="follower">The subscriber to be removed</param>
        /// <returns>AsyncCompletion for this operation</returns>
        Task RemoveFollower(string userAlias, IChirperSubscriber follower);
    }
}
