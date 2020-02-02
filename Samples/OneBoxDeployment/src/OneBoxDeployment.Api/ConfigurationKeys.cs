using System.Collections.Immutable;

namespace OneBoxDeployment.Api
{
    /// <summary>
    /// Predefined configuration keys used to control OneBoxDeployment program flow.
    /// </summary>
    public static class ConfigurationKeys
    {
        /// <summary>
        /// <see cref="Startup"/> recognizes this key and adds a faulty route with the given parameters.
        /// </summary>
        /// <remarks>If this key is found in production environment while starting, the application halts.</remarks>
        public const string AlwaysFaultyRoute = nameof(AlwaysFaultyRoute);

        /// <summary>
        /// These keys should not be used, and hence present, in a production deployment.
        /// </summary>
        public static ImmutableList<string> ConfigurationKeysForbiddenInProduction { get; } = ImmutableList.CreateRange(new[]
        {
            AlwaysFaultyRoute
        });
    }
}
