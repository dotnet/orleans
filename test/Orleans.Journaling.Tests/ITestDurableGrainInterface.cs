namespace Orleans.Journaling.Tests;

/// <summary>
/// Interface for the test durable grain
/// </summary>
public interface ITestDurableGrainInterface : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task SetValues(string name, int counter);
    Task<(string Name, int Counter)> GetValues();
}