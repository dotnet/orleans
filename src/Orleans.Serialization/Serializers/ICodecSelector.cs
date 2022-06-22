using System;

namespace Orleans.Serialization.Serializers;

/// <summary>
/// Functionality used by general-purpose codecs (such as a JSON codec) to allow types to opt-in to using them.
/// </summary>
public interface ICodecSelector
{
    /// <summary>
    /// The well-known codec name, used to match an instance with a codec.
    /// </summary>
    public string CodecName { get; }

    /// <summary>
    /// Returns true if the specified codec should be used for this type.
    /// </summary>
    public bool IsSupportedType(Type type);
}

/// <summary>
/// Functionality used by general-purpose copiers (such as a JSON copier) to allow types to opt-in to using them.
/// </summary>
public interface ICopierSelector
{
    /// <summary>
    /// The well-known copier name, used to match an instance with a copier.
    /// </summary>
    public string CopierName { get; }

    /// <summary>
    /// Returns true if the specified copier should be used for this type.
    /// </summary>
    public bool IsSupportedType(Type type);
}

/// <summary>
/// Implementation of <see cref="ICodecSelector"/> which uses a delegate.
/// </summary>
public sealed class DelegateCodecSelector : ICodecSelector
{
    public string CodecName { get; init; }

    public Func<Type, bool> IsSupportedTypeDelegate { get; init; }

    public bool IsSupportedType(Type type) => IsSupportedTypeDelegate(type);
}

/// <summary>
/// Implementation of <see cref="ICopierSelector"/> which uses a delegate.
/// </summary>
public sealed class DelegateCopierSelector : ICopierSelector
{
    public string CopierName { get; init; }

    public Func<Type, bool> IsSupportedTypeDelegate { get; init; }

    public bool IsSupportedType(Type type) => IsSupportedTypeDelegate(type);
}