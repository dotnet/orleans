using System;
using Orleans;
using Orleans.Runtime;

namespace OrleansEventSourcing.CustomStorage;

/// <summary>
/// Exception thrown whenever a grain call is attempted with a bad / missing custom storage provider configuration settings for that grain.
/// </summary>
[GenerateSerializer, Serializable]
public sealed class BadCustomStorageProviderConfigException : OrleansException
{
    public BadCustomStorageProviderConfigException()
    {
    }

    public BadCustomStorageProviderConfigException(string msg)
        : base(msg)
    {
    }

    public BadCustomStorageProviderConfigException(string msg, Exception exc)
        : base(msg, exc)
    {
    }
}