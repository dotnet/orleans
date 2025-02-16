// <copyright file="DefaultDocumentIdProvider.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

// namespace left intentionally : historical reasons
using Orleans.Persistence.Cosmos.DocumentIdProviders;

namespace Orleans.Persistence.Cosmos;

#pragma warning disable SA1310 // Field names should not contain underscore

/// <summary>
/// The default implementation of <see cref="IDocumentIdProvider"/>.
/// </summary>
public sealed class DefaultDocumentIdProvider : DocumentIdProviderBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultDocumentIdProvider"/> class.
    /// </summary>
    /// <param name="options">The documentId provider options.</param>
    public DefaultDocumentIdProvider(IOptions<DocumentIdProviderOptions> options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // In PROD it should be null, we will not prefix item IDs with the service ID.
        this.serviceId = options.Value?.ServiceId;
    }
}

/// <summary>
/// The inference pools CosmosDb configuration section.
/// </summary>
public sealed class DocumentIdProviderOptions
{
    private string? servId;

    /// <summary>
    /// Gets or sets the DocumentId provider's ServiceId.
    /// </summary>
    /// <note>
    /// It should be NULL for PROD.
    /// </note>
    public string? ServiceId
    {
        get => this.servId;
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                this.servId = null;

                return;
            }

            if (value.Length > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The string is too long.");
            }

            foreach (var c in value)
            {
                var isValid = c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c == '-';

                if (!isValid)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "The string has invalid characters.");
                }
            }

            this.servId = value;
        }
    }
}