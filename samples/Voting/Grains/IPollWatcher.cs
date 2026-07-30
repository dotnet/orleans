namespace VotingContract;

public interface IPollWatcher : IGrainObserver
{
    void OnPollUpdated(PollState state);
}
