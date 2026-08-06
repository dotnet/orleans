using Microsoft.Extensions.Options;
using BlazorWasm.Models;
using System.Net.Http.Json;

namespace BlazorWasm.Services;

public class ApiService(IOptions<ApiServiceOptions> options, HttpClient client)
{
    private readonly ApiServiceOptions _options = options.Value;

    public Task<WeatherInfo[]?> GetWeatherForecastAsync() =>
        client.GetFromJsonAsync<WeatherInfo[]>($"{_options.BaseAddress}/Weather");

    public Task<IEnumerable<TodoItem>?> GetTodosAsync(Guid ownerKey) =>
        client.GetFromJsonAsync<IEnumerable<TodoItem>>($"{_options.BaseAddress}/todo/list/{ownerKey}");

    public Task SetTodoAsync(TodoItem item) =>
        client.PostAsJsonAsync($"{_options.BaseAddress}/todo", item);

    public Task DeleteTodoAsync(Guid itemKey) =>
        client.DeleteAsync($"{_options.BaseAddress}/todo/{itemKey}");
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
