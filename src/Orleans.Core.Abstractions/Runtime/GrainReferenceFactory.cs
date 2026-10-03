namespace Orleans.Runtime;

/// <summary>
/// Constructs a grain reference using shared runtime state and a grain key.
/// </summary>
/// <param name="shared">The runtime state shared by references for a grain type and interface.</param>
/// <param name="key">The key identifying the target grain.</param>
/// <returns>A new grain reference.</returns>
public delegate GrainReference GrainReferenceFactory(GrainReferenceShared shared, IdSpan key);
