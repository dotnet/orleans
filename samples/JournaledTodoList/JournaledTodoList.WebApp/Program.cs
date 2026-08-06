using JournaledTodoList.WebApp.Components;
using JournaledTodoList.WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedAzureTableServiceClient("clustering");
builder.AddKeyedAzureBlobServiceClient("grain-state");
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault();
    siloBuilder.AddStateStorageBasedLogConsistencyProvider(name: Constants.StateStorageProviderName);
});
builder.Services.AddScoped<TodoListService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
