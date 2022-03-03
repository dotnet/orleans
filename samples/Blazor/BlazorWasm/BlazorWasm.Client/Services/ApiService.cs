using Microsoft.Extensions.Options;
using BlazorWasm.Models;
using System.Net.Http.Json;

namespace BlazorWasm.Services;

public class ApiService
{
    private readonly HttpClient _client;
    private readonly ApiServiceOptions _options;

    public ApiService(IOptions<ApiServiceOptions> options, HttpClient client)
    {
        _client = client;
        _options = options.Value;
    }

    public Task<WeatherInfo[]?> GetWeatherForecastAsync() =>
        _client.GetFromJsonAsync<WeatherInfo[]>($"{_options.BaseAddress}/Weather");

    public Task<IEnumerable<TodoItem>?> GetTodosAsync(Guid ownerKey) =>
        _client.GetFromJsonAsync<IEnumerable<TodoItem>>($"{_options.BaseAddress}/todo/list/{ownerKey}");

    public Task SetTodoAsync(TodoItem item) =>
        _client.PostAsJsonAsync($"{_options.BaseAddress}/todo", item);

    public Task DeleteTodoAsync(Guid itemKey) =>
        _client.DeleteAsync($"{_options.BaseAddress}/todo/{itemKey}");
}

public static class ApiServiceBuilderExtensions
{
    public static IServiceCollection AddApiService(
        this IServiceCollection services,
        Action<ApiServiceOptions>? configureOptions = null)
    {
        services.AddSingleton<ApiService>();
        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }

        return services;
    }
}
