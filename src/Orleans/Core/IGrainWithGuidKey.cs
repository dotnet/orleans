using System;

namespace Orleans
{
    /// <summary>
    /// Marker interface for grains with <see cref="Guid"/> keys.
    /// </summary>
    public interface IGrainWithGuidKey : IGrain
    {
    }
}