using Orleans.Dashboard;
using Voting.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseOrleans((ctx, orleansBuilder) => orleansBuilder
        .UseLocalhostClustering()
        .AddMemoryGrainStorage("votes")
        .AddDashboard());

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddScoped<PollService>();
builder.Services.AddScoped<DemoService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapRazorPages();
app.MapOrleansDashboard("/dashboard");
app.MapFallbackToPage("/_Host");
app.Run();
