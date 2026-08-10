namespace FirestoreSample;

public interface ICounterGrain : IGrainWithStringKey
{
    Task<int> Increment();

    Task EnsureReminder();
}
