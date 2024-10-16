using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.UseOrleansClient(clientBuilder => clientBuilder.UseLocalhostClustering());
builder.Services.AddControllers();

var app = builder.Build();

var options = new DefaultFilesOptions();
options.DefaultFileNames.Clear();
options.DefaultFileNames.Add("index.html");

app.UseDefaultFiles(options);
app.UseStaticFiles();
app.MapControllers();
app.Run();
