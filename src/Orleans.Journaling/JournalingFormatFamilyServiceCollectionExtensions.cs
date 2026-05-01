using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Journaling;

/// <summary>
/// Extension methods for registering journaling format families.
/// </summary>
public static class JournalingFormatFamilyServiceCollectionExtensions
{
    /// <summary>
    /// Adds services for a journaling format family.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="key">The log format key.</param>
    /// <returns>A builder for adding format-family services.</returns>
    public static JournalingFormatFamilyBuilder AddJournalingFormatFamily(this IServiceCollection services, string key)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new JournalingFormatFamilyBuilder(services, LogFormatServices.ValidateLogFormatKey(key), tryAdd: false);
    }

    internal static JournalingFormatFamilyBuilder TryAddJournalingFormatFamily(this IServiceCollection services, string key)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new JournalingFormatFamilyBuilder(services, LogFormatServices.ValidateLogFormatKey(key), tryAdd: true);
    }
}

/// <summary>
/// Builds service registrations for one journaling format family.
/// </summary>
public sealed class JournalingFormatFamilyBuilder
{
    private readonly bool _tryAdd;

    internal JournalingFormatFamilyBuilder(IServiceCollection services, string key, bool tryAdd)
    {
        Services = services;
        Key = key;
        _tryAdd = tryAdd;
    }

    /// <summary>
    /// Gets the service collection being configured.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Gets the log format key for this family.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Adds the log format implementation for this family.
    /// </summary>
    /// <typeparam name="TFormat">The log format implementation type.</typeparam>
    /// <returns>This builder.</returns>
    public JournalingFormatFamilyBuilder AddLogFormat<TFormat>()
        where TFormat : class, ILogFormat
    {
        if (_tryAdd)
        {
            Services.TryAddSingleton<TFormat>();
            Services.TryAddKeyedSingleton<ILogFormat>(Key, static (sp, _) => sp.GetRequiredService<TFormat>());
            Services.TryAddSingleton<ILogFormat>(static sp => sp.GetRequiredService<TFormat>());
        }
        else
        {
            Services.AddSingleton<TFormat>();
            Services.AddKeyedSingleton<ILogFormat>(Key, static (sp, _) => sp.GetRequiredService<TFormat>());
            Services.AddSingleton<ILogFormat>(static sp => sp.GetRequiredService<TFormat>());
        }

        return this;
    }

    /// <summary>
    /// Adds the log format instance for this family.
    /// </summary>
    /// <param name="logFormat">The log format instance.</param>
    /// <returns>This builder.</returns>
    public JournalingFormatFamilyBuilder AddLogFormat(ILogFormat logFormat)
    {
        ArgumentNullException.ThrowIfNull(logFormat);
        if (_tryAdd)
        {
            Services.TryAddKeyedSingleton(Key, logFormat);
            Services.TryAddSingleton(logFormat);
        }
        else
        {
            Services.AddKeyedSingleton(Key, logFormat);
            Services.AddSingleton(logFormat);
        }

        return this;
    }

    /// <summary>
    /// Adds the durable operation codec provider implementation for this family.
    /// </summary>
    /// <typeparam name="TProvider">The operation codec provider implementation type.</typeparam>
    /// <returns>This builder.</returns>
    public JournalingFormatFamilyBuilder AddOperationCodecProvider<TProvider>()
        where TProvider :
            class,
            IDurableDictionaryOperationCodecProvider,
            IDurableListOperationCodecProvider,
            IDurableQueueOperationCodecProvider,
            IDurableSetOperationCodecProvider,
            IDurableValueOperationCodecProvider,
            IDurableStateOperationCodecProvider,
            IDurableTaskCompletionSourceOperationCodecProvider
        => AddOperationCodecProvider<TProvider>(static sp => ActivatorUtilities.CreateInstance<TProvider>(sp));

    /// <summary>
    /// Adds the durable operation codec provider implementation for this family.
    /// </summary>
    /// <typeparam name="TProvider">The operation codec provider implementation type.</typeparam>
    /// <param name="factory">The factory used to create the provider.</param>
    /// <returns>This builder.</returns>
    public JournalingFormatFamilyBuilder AddOperationCodecProvider<TProvider>(Func<IServiceProvider, TProvider> factory)
        where TProvider :
            class,
            IDurableDictionaryOperationCodecProvider,
            IDurableListOperationCodecProvider,
            IDurableQueueOperationCodecProvider,
            IDurableSetOperationCodecProvider,
            IDurableValueOperationCodecProvider,
            IDurableStateOperationCodecProvider,
            IDurableTaskCompletionSourceOperationCodecProvider
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_tryAdd)
        {
            Services.TryAddSingleton(factory);
            Services.TryAddKeyedSingleton<IDurableDictionaryOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.TryAddKeyedSingleton<IDurableListOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.TryAddKeyedSingleton<IDurableQueueOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.TryAddKeyedSingleton<IDurableSetOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.TryAddKeyedSingleton<IDurableValueOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.TryAddKeyedSingleton<IDurableStateOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.TryAddKeyedSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.TryAddSingleton<IDurableDictionaryOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.TryAddSingleton<IDurableListOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.TryAddSingleton<IDurableQueueOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.TryAddSingleton<IDurableSetOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.TryAddSingleton<IDurableValueOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.TryAddSingleton<IDurableStateOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.TryAddSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
        }
        else
        {
            Services.AddSingleton(factory);
            Services.AddKeyedSingleton<IDurableDictionaryOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.AddKeyedSingleton<IDurableListOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.AddKeyedSingleton<IDurableQueueOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.AddKeyedSingleton<IDurableSetOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.AddKeyedSingleton<IDurableValueOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.AddKeyedSingleton<IDurableStateOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.AddKeyedSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(Key, static (sp, _) => sp.GetRequiredService<TProvider>());
            Services.AddSingleton<IDurableDictionaryOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.AddSingleton<IDurableListOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.AddSingleton<IDurableQueueOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.AddSingleton<IDurableSetOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.AddSingleton<IDurableValueOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.AddSingleton<IDurableStateOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
            Services.AddSingleton<IDurableTaskCompletionSourceOperationCodecProvider>(static sp => sp.GetRequiredService<TProvider>());
        }

        return this;
    }
}
