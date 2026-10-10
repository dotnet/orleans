using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.DurableMessaging;
using Orleans.Serialization;

namespace Orleans.Hosting;

/// <summary>Extensions for registering typed durable message subjects.</summary>
public static class DurableMessageTypeExtensions
{
    /// <summary>Registers a singleton subject binding, keyed by its ordinal subject.</summary>
    /// <typeparam name="T">The subject's payload contract.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="subject">The exact application protocol subject.</param>
    /// <returns>The service collection.</returns>
    /// <exception cref="InvalidOperationException">A subject binding is already registered.</exception>
    public static IServiceCollection AddDurableMessageType<T>(this IServiceCollection services, string subject)
    {
        ArgumentNullException.ThrowIfNull(services);
        DurableEnvelopeValidation.ValidateSubject(subject);
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService
                && descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(DurableMessageType<>)
                && Equals(descriptor.ServiceKey, subject))
            {
                throw new InvalidOperationException($"A payload contract is already registered for durable message subject '{subject}'.");
            }
        }

        services.TryAddSingleton<Serializer<T>>(static provider => provider.GetRequiredService<Serializer>().GetSerializer<T>());
        services.AddKeyedSingleton<DurableMessageType<T>>(subject,
            (provider, _) => new DurableMessageType<T>(subject, provider.GetRequiredService<Serializer<T>>()));
        return services;
    }
}
