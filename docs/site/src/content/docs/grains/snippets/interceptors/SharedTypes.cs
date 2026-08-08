using Orleans;

namespace Orleans.Docs.Snippets.Interceptors;

// Shared interfaces used across filter examples
public interface IMyGrain : IGrainWithIntegerKey
{
    Task<int> MyInterceptedMethod();
}

public interface IMyFilteredGrain : IGrainWithIntegerKey
{
    Task<int> GetFavoriteNumber();
}

public interface ICallAuditGrain : IGrainWithStringKey
{
    Task OnReceivedCall(string methodName);
}

// Logger factory placeholder (matches legacy Orleans Logger pattern used in docs)
public delegate T Factory<TInput, T>(TInput input);
public class Logger
{
    public void Info(string message) { }
}
