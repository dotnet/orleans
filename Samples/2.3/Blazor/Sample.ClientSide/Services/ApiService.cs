using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sample.ClientSide.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace Sample.ClientSide.Services
{
    public class ApiService
    {
        private readonly HttpClient client;
        private readonly ApiServiceOptions options;

        public ApiService(IOptions<ApiServiceOptions> options, HttpClient client)
        {
            this.client = client;
            this.options = options.Value;
        }

        public Task<WeatherInfo[]> GetWeatherForecastAsync() =>
            client.GetJsonAsync<WeatherInfo[]>($"{options.BaseAddress}/Weather");

        public Task<IEnumerable<TodoItem>> GetTodosAsync(Guid ownerKey) =>
            client.GetJsonAsync<IEnumerable<TodoItem>>($"{options.BaseAddress}/todo/list/{ownerKey}");

        public Task SetTodoAsync(TodoItem item) =>
            client.PostJsonAsync($"{options.BaseAddress}/todo", item);

        public Task DeleteTodoAsync(Guid itemKey) =>
            client.DeleteAsync($"{options.BaseAddress}/todo/{itemKey}");
    }

    public static class ApiServiceBuilderExtensions
    {
        public static IServiceCollection AddApiService(this IServiceCollection services, Action<ApiServiceOptions> configureOptions = null)
        {
            services.AddSingleton<ApiService>();
            if (configureOptions != null)
            {
                services.Configure(configureOptions);
            }
            return services;
        }
    }
}