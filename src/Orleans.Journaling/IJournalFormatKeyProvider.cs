namespace Orleans.Journaling;

/// <summary>
/// Provides the journal format key to use for a grain.
/// </summary>
public interface IJournalFormatKeyProvider
{
    /// <summary>
    /// Gets the journal format key to use for the provided <paramref name="grainContext"/>.
    /// </summary>
    /// <param name="grainContext">The grain context.</param>
    /// <returns>The journal format key.</returns>
    string GetJournalFormatKey(IGrainContext grainContext);
}
