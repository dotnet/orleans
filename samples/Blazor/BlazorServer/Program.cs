using BlazorServer.Services;
using Orleans.Hosting;
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans((ctx, siloBuilder) => {

    siloBuilder.UseLocalhostClustering();
    siloBuilder.AddMemoryGrainStorageAsDefault();
    siloBuilder.AddSimpleMessageStreamProvider("SMS");
    siloBuilder.AddMemoryGrainStorage("PubSubStore");
});

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddSingleton<TodoService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();