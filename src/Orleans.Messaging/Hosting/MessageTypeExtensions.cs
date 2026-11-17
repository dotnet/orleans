using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Messaging;
using Orleans.Serialization;

namespace Orleans.Hosting;

/// <summary>Extensions for registering typed durable message subjects.</summary>
public static class MessageTypeExtensions
{
    /// <summary>Registers a singleton subject binding, keyed by its ordinal subject.</summary>
    /// <typeparam name="T">The subject's payload contract.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="subject">The exact application protocol subject.</param>
    /// <returns>The service collection.</returns>
    /// <exception cref="InvalidOperationException">A subject binding is already registered.</exception>
    public static IServiceCollection AddMessageType<T>(this IServiceCollection services, string subject)
    {
        ArgumentNullException.ThrowIfNull(services);
        EnvelopeValidation.ValidateSubject(subject);
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService
                && descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(MessageType<>)
                && Equals(descriptor.ServiceKey, subject))
            {
                throw new InvalidOperationException($"A payload contract is already registered for durable message subject '{subject}'.");
            }
        }

        services.TryAddSingleton<Serializer<T>>(static provider => provider.GetRequiredService<Serializer>().GetSerializer<T>());
        services.AddKeyedSingleton<MessageType<T>>(subject,
            (provider, _) => new MessageType<T>(subject, provider.GetRequiredService<Serializer<T>>()));
        return services;
    }
}
