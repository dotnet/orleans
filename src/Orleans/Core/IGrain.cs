using System;

namespace Orleans
{
    using Orleans.Runtime;

    /// <summary>
    /// Marker interface for grains
    /// </summary>
    public interface IGrain : IAddressable
    {
    }

    /// <summary>
    /// Marker interface for grains with <see cref="Guid"/> keys.
    /// </summary>
    public interface IGrainWithGuidKey : IGrain
    {
    }

    /// <summary>
    /// Marker interface for grains with <see cref="Int64"/> keys.
    /// </summary>
    public interface IGrainWithIntegerKey : IGrain
    {
    }

    /// <summary>
    /// Marker interface for grains with <see cref="String"/> keys.
    /// </summary>
    public interface IGrainWithStringKey : IGrain
    {
    }

    /// <summary>
    /// Marker interface for grains with compound keys.
    /// </summary>
    public interface IGrainWithGuidCompoundKey : IGrain
    {
    }

    /// <summary>
    /// Marker interface for grains with compound keys.
    /// </summary>
    public interface IGrainWithIntegerCompoundKey : IGrain
    {
    }
}
