namespace Orleans.Runtime;

/// <summary>
/// Indicates that a request to the grain directory was rejected because the target grain or directory range 
/// is currently under a safety lease hold after an ungraceful silo crash.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DirectoryLeaseHoldException"/> class.
/// </remarks>
/// <param name="message">The message.</param>
[Serializable]
[GenerateSerializer]
[Alias("DirectoryLeaseHoldException")]
public class DirectoryLeaseHoldException(string message) : OrleansException(message);
