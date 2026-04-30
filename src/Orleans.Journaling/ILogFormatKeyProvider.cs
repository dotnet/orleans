namespace Orleans.Journaling;

/// <summary>
/// Provides the log format key to use for a grain.
/// </summary>
public interface ILogFormatKeyProvider
{
    /// <summary>
    /// Gets the log format key to use for the provided <paramref name="grainContext"/>.
    /// </summary>
    /// <param name="grainContext">The grain context.</param>
    /// <returns>The log format key.</returns>
    string GetLogFormatKey(IGrainContext grainContext);
}
