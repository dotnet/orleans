// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License.

namespace Orleans.ShoppingCart.Silo.Services;

public class BaseClusterService
{
    private readonly IHttpContextAccessor _httpContextAccessor = null!;
    protected readonly IClusterClient _client = null!;

    public BaseClusterService(
        IHttpContextAccessor httpContextAccessor, IClusterClient client) =>
        (_httpContextAccessor, _client) = (httpContextAccessor, client);

    protected T TryUseGrain<TGrainInterface, T>(
        Func<TGrainInterface, T> useGrain, Func<T> defaultValue)
        where TGrainInterface : IGrainWithStringKey =>
         TryUseGrain(
             useGrain,
             _httpContextAccessor.TryGetUserId(),
             defaultValue);

    protected T TryUseGrain<TGrainInterface, T>(
        Func<TGrainInterface, T> useGrain,
        string? key,
        Func<T> defaultValue)
        where TGrainInterface : IGrainWithStringKey =>
        key is { Length: > 0 } primaryKey
            ? useGrain.Invoke(_client.GetGrain<TGrainInterface>(primaryKey))
            : defaultValue.Invoke();
}
