namespace GoogleFirestore;

public interface ICounterGrain : IGrainWithStringKey
{
    Task<int> Increment();

    Task EnsureReminder();
}
