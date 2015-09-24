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

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Samples.Chirper.GrainInterfaces
{
    /// <summary>
    /// Orleans grain interface IChirperPublisher
    /// </summary>
    public interface IChirperPublisher : IGrainWithIntegerKey
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
