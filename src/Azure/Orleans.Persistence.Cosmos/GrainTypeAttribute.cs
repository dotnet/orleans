// <copyright file="GrainTypeAttribute.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Defines the grain type for the grain class which this attribute annotates.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GrainTypeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GrainTypeAttribute"/> class.
    /// </summary>
    /// <param name="grainType">
    /// The grain type name that must be a lowercase alphanumeric string.
    /// </param>
    public GrainTypeAttribute(string grainType)
    {
        if (grainType == null)
        {
            throw new ArgumentNullException(nameof(grainType));
        }

        if (grainType.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grainType), "The value is an empty string.");
        }

        if (grainType.Length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(grainType), "The string is too long.");
        }

        foreach (var c in grainType)
        {
            bool isValid = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z');

            if (!isValid)
            {
                throw new ArgumentOutOfRangeException(nameof(grainType), "The value must be a lowercase alphanumeric string.");
            }
        }

        this.GrainType = grainType;
    }

    /// <summary>
    /// Gets the grain type value.
    /// </summary>
    public string GrainType { get; }
}
