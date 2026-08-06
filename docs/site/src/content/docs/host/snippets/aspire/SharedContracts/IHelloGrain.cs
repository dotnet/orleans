namespace Orleans.Docs.Snippets.Aspire.SharedContracts;

// Simple grain interface for Aspire integration examples
public interface IHelloGrain : IGrainWithStringKey
{
    Task<string> SayHelloAsync(string name);
}
