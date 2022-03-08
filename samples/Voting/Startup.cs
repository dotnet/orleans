public class Startup
{
    public void ConfigureServices(IServiceCollection services) =>
        services.AddControllersWithViews();

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env) =>
        app.UseStaticFiles()
            .UseRouting()
            .UseEndpoints(
                endpoints => endpoints.MapDefaultControllerRoute());
}
