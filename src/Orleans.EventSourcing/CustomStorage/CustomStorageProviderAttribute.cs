using System;
using Orleans.Providers;

namespace OrleansEventSourcing.CustomStorage;

[AttributeUsage(AttributeTargets.Class)]
public class CustomStorageProviderAttribute : Attribute
{
    public string ProviderName { get; set; } = ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME;
}