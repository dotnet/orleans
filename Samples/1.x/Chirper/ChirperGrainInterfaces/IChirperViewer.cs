namespace Orleans.Samples.Chirper.GrainInterfaces
{
    /// <summary>
    /// Orleans observer interface IChirperViewer
    /// </summary>
    public interface IChirperViewer : IGrainObserver
    {
        /// <summary>Notification that a new Chirp has been received from one of the accounts this user is following</summary>
        /// <param name="chirp">Message data for this chirp</param>
        void NewChirpArrived(ChirperMessage chirp);

        /// <summary>A new subscription has been added by this user alias</summary>
        /// <param name="following">User alias of the user now been followed</param>
        void SubscriptionAdded(ChirperUserInfo following);

        /// <summary>Unsubscribe from receiving notifications of new Chirps sent by this publisher</summary>
        /// <param name="notFollowing">User alias of the user no longer been followed</param>
        void SubscriptionRemoved(ChirperUserInfo notFollowing);

    }
}
