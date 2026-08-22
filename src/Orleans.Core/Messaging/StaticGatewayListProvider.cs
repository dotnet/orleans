using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Messaging;

/// <summary>
/// <see cref="IGatewayListProvider"/> implementation which returns a static list, configured via <see cref="StaticGatewayListProviderOptions"/>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="StaticGatewayListProvider"/> class.
/// </remarks>
/// <param name="options">The specific options.</param>
/// <param name="gatewayOptions">The general gateway options.</param>
public class StaticGatewayListProvider(IOptions<StaticGatewayListProviderOptions> options, IOptions<GatewayOptions> gatewayOptions) : IGatewayListProvider
{
    private readonly StaticGatewayListProviderOptions _options = options.Value;
    private readonly TimeSpan _maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;

    /// <inheritdoc />
    public Task InitializeGatewayListProvider() => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IList<Uri>> GetGateways() => Task.FromResult<IList<Uri>>(_options.Gateways);

    /// <inheritdoc />
    public TimeSpan MaxStaleness => _maxStaleness;

    /// <inheritdoc />
    public bool IsUpdatable => true;
}
