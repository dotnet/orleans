using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Samples.Chirper.GrainInterfaces
{
    /// <summary>
    /// Orleans grain interface IChirperAccount -- This is the user-facing control grain for a particular user. 
    /// A suitably authenticated external application can perform both publisher and subscriber functions on behalf of a user through this grain.
    /// This grain mediates command and communications between the external application and that user's publisher & subscriber agent grains.
    /// </summary>
    public interface IChirperAccount : IChirperPublisher, IChirperSubscriber
    {
        /// <summary>
        /// Update user info from supplied data. Null fields mean leave current values unchanged.
        /// </summary>
        /// <param name="userInfo">User info to be updated</param>
        /// <returns>Task status for this operation</returns>
        Task SetUserDetails(ChirperUserInfo userInfo);

        ///// <summary>Add a new follower subscription from this user to the specified publisher</summary>
        ///// <param name="userAliasToFollow">The alias of the new publisher now being followed by this user</param>
        ///// <returns>Task status for this operation</returns>
        //Task FollowUserAlias(string userAliasToFollow);

        /// <summary>Add a new follower subscription from this user to the specified publisher</summary>
        /// <param name="userIdToFollow">The Id of the new publisher now being followed by this user</param>
        /// <returns>Task status for this operation</returns>
        Task FollowUserId(long userIdToFollow);

        ///// <summary>Remove a follower subscription from this user to the specified publisher</summary>
        ///// <param name="userAliasToUnfollow">The alias of the publisher no longer being followed by this user</param>
        ///// <returns>Task status for this operation</returns>
        //Task UnfollowUserAlias(string userAliasToUnfollow);

        /// <summary>Remove a follower subscription from this user to the specified publisher</summary>
        /// <param name="userIdToUnfollow">The id of the publisher no longer being followed by this user</param>
        /// <returns>AsyncCompletion status for this operation</returns>
        Task UnfollowUserId(long userIdToUnfollow);

        /// <summary>Get the list of publishers who this user is following</summary>
        /// <returns>List of users being followed</returns>
        Task<List<ChirperUserInfo>> GetFollowingList();

        /// <summary>Get the list of subscribers who are following this user</summary>
        /// <returns>List of users who are following this user</returns>
        Task<List<ChirperUserInfo>> GetFollowersList();

        /// <summary>Publish a new Chirp message</summary>
        /// <param name="chirpMessage">The message text to be published as a new Chirp</param>
        /// <returns>Completion status for the publish operation</returns>
        Task PublishMessage(string chirpMessage);

        /// <summary>Request the most recent 'n' Chirp messages received by this subscriber, from the specified start position.</summary>
        /// <param name="n">Number of Chirp messages requested. A value of -1 means all messages.</param>
        /// <param name="start">The start position for returned messages. A value of 0 means start with most recent message. A positive value means skip past that many of the most recent messages</param>
        /// <returns>List of Chirp messages received by this subscriber</returns>
        /// <remarks>The subscriber might only return a partial record of historic events due to message retention policies.</remarks>
        Task<List<ChirperMessage>> GetReceivedMessages(int n = 10, int start = 0);

        /// <summary>Subscribe a viewer app to receive notifications of new Chirps received by this user</summary>
        /// <param name="viewer">Observer for new Chirps notifications</param>
        /// <returns>AsyncCompletion for the subscribe operation</returns>
        Task ViewerConnect(IChirperViewer viewer);

        /// <summary>Unsubscribe a viewer app from receiving notifications of new Chirps received by this user</summary>
        /// <param name="viewer">Observer for new Chirps notifications</param>
        /// <returns>AsyncCompletion for the unsubscribe operation</returns>
        Task ViewerDisconnect(IChirperViewer viewer);
    }
}
