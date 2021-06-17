using Hellang.Middleware.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using OneBoxDeployment.Api.Filters;
using OneBoxDeployment.Api.Logging;
using OneBoxDeployment.Common;
using OneBoxDeployment.GrainInterfaces;
using OneBoxDeployment.OrleansUtilities;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Statistics;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;


namespace OneBoxDeployment.Api
{
    /// <summary>
    /// An API startup class with modifications.
    /// </summary>
    public class Startup
    {
        /// <summary>
        /// This is the root to Swagger documentation URL and to the
        /// generated content.
        /// </summary>
        private string SwaggerRoot { get; } = "api-docs";

        /// <summary>
        /// The root for Swagger documentation URL.
        /// </summary>
        private string SwaggerDocumentationBasePath { get; } = "OneBoxDeployment";

        /// <summary>
        /// The environment specific configuration object.
        /// </summary>
        public IConfiguration Configuration { get; }

        /// <summary>
        /// The environment specific logger.
        /// </summary>
        public Microsoft.Extensions.Logging.ILogger Logger { get; }

        /// <summary>
        /// The hosting environment.
        /// </summary>
        public IWebHostEnvironment Environment { get; set; }


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="logger">The environment specific logger.</param>
        /// <param name="configuration">The environment specific configuration object.</param>
        /// <param name="env">The environment information to use in checking per deployment type configuration value validity.</param>
        public Startup(ILogger<Startup> logger, IConfiguration configuration, IWebHostEnvironment env)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            Environment = env ?? throw new ArgumentNullException(nameof(env));

            if(Environment.IsProduction())
            {
                foreach(var forbiddenKey in ConfigurationKeys.ConfigurationKeysForbiddenInProduction)
                {
                    var forbiddenKeys = new List<string>();
                    if(configuration.GetValue<string>(forbiddenKey, null) != null)
                    {
                        forbiddenKeys.Add(forbiddenKey);
                    }

                    //Note: ConfigurationErrorsException could be thrown here, but it'd require taking
                    //a dependency to System.Configuration.
                    if(forbiddenKeys.Any())
                    {
                        throw new ArgumentException($"The following keys are forbidden in production " +
                                                    $"= {env.EnvironmentName}: {string.Join(',', forbiddenKeys)}", nameof(configuration));
                    }
                }
            }
        }


        /// <summary>
        /// This method gets called by the runtime. Use this method to add services to the container.
        /// </summary>
        /// <param name="services">The ASP.NET services collection.</param>
        public void ConfigureServices(IServiceCollection services)
        {
            if(Environment.IsProduction())
            {
                services.AddApplicationInsightsTelemetry(Configuration);
                services.AddCors(options =>
                {
                    var allowedOrigins = Configuration.GetSection("AllowedOrigins").Get<string[]>();
                    options.AddPolicy("CorsPolicy",
                        builder => builder
                            .WithOrigins(allowedOrigins)
                            .AllowAnyMethod()
                            .AllowAnyHeader());
                });
            }
            else
            {
                services.AddCors(options =>
                {
                    options.AddPolicy("CorsPolicy",
                        builder => builder
                            .AllowAnyOrigin()
                            .AllowAnyMethod()
                            .AllowAnyHeader());
                });
            }

            //See more about formatters at https://docs.microsoft.com/en-us/aspnet/core/mvc/models/formatting.
            services
                .AddProblemDetails(ConfigureProblemDetails)
                .AddMvcCore(options =>
                {
                    if(!Environment.IsDevelopment())
                    {
                        options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                    }

                    options.Conventions.Add(new NotFoundResultFilterConvention());

                    //The Content-Security-Policy (CSP) is JSON, so it's added here to the known JSON serialized types.
                    var jsonInputFormatter = options.InputFormatters.OfType<SystemTextJsonInputFormatter>().FirstOrDefault();
                    if(jsonInputFormatter != null)
                    {
                        jsonInputFormatter.SupportedMediaTypes.Add(MimeTypes.MimeTypeCspReport);
                    }
                })
                .AddApiExplorer()
                //.AddAuthorization()
                .AddDataAnnotations()
                .AddFormatterMappings();

            //For further Swagger registration information:
            //https://docs.microsoft.com/en-us/aspnet/core/tutorials/web-api-help-pages-using-swagger.
            services.AddSwaggerGen(openApiConfig =>
            {
                openApiConfig.DescribeAllParametersInCamelCase();

                //In the Swagger document this corresponds as follows:
                //http://localhost:4003/{SwaggerRoot}/{SwaggerDocumentationBasePath}/swagger.json
                openApiConfig.SwaggerDoc(SwaggerDocumentationBasePath, new OpenApiInfo
                {
                    Title = "OneBoxDeployment APIs",

                    //Note that this is information subject to change and with legal
                    //consequences. In the future there needs to be a formal way to handle
                    //these.
                    //Also, for choosing licenses:
                    //- https://go.developer.ebay.com/api-license-agreement
                    //- https://www.zendesk.com/company/customers-partners/application-developer-api-license-agreement/
                    Contact = new OpenApiContact
                    {
                        Email = "contact@contact.com",
                        Name = "OneBoxDeployment",
                        Url = new Uri("https://contactinformationtobedefined.com")
                    },
                    Description = "OneBoxDeployment application programming interface (API) descriptions.",
                    License = new OpenApiLicense
                    {
                        Name = "The license name to be defined.",
                        Url = new Uri("https://contactinformationtobedefined.com")
                    },
                    TermsOfService = new Uri("https://choosealicense.com/licenses/")
                });


                //The name of the comments file (see project properties). This is set automatically to the name
                //of the project unless explicitly changed.
                string commentsFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                string fullCommentsFilePath = Path.Combine(AppContext.BaseDirectory, commentsFilename);
                openApiConfig.IncludeXmlComments(fullCommentsFilePath, includeControllerXmlComments: true);
            });

            services.AddHttpContextAccessor();
            services.AddTransient<IPrincipal>(provider => provider.GetService<IHttpContextAccessor>()?.HttpContext?.User);

            //A trick since the cluster configuration value from the in-memory provider is bound differently
            //to the JSON one. Here in-memory one takes presedence to the file one since in that case the values
            //come from the tests. Should be a flag or better yet, override properly...
            ClusterConfig clusterConfig = null;
            var clusterConfigValue = Configuration.GetSection(nameof(ClusterConfig));
            if(clusterConfigValue?.Value != null)
            {
                var deserializeOptions = new JsonSerializerOptions();
                deserializeOptions.Converters.Add(new IPAddressConverter());
                clusterConfig = JsonSerializer.Deserialize<ClusterConfig>(clusterConfigValue.Value, deserializeOptions);
            }
            else
            {
                clusterConfig = Configuration.GetSection(nameof(ClusterConfig)).GetValid<ClusterConfig>();
            }

            services.AddSingleton(Environment);
            services.AddSingleton(Configuration);

            var clientBuilder = new ClientBuilder()
               .ConfigureLogging(logging => logging.AddConsole())
               .UsePerfCounterEnvironmentStatistics()
               .Configure<ClusterOptions>(options =>
               {
                   options.ClusterId = clusterConfig.ClusterOptions.ClusterId;
                   options.ServiceId = clusterConfig.ClusterOptions.ServiceId;
               })
               .UseAdoNetClustering(options =>
               {
                   options.Invariant = clusterConfig.ConnectionConfig.AdoNetConstant;
                   options.ConnectionString = clusterConfig.ConnectionConfig.ConnectionString;
               })
               .Configure<ClientMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromSeconds(30))
               .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(ITestStateGrain).Assembly).WithReferences());

            var client = clientBuilder.Build();
            client.Connect(async ex =>
            {
                Logger.LogWarning("Could not connect to Orleans cluster: {exception}", ex);
                await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
                return true;
            }).GetAwaiter().GetResult();
            services.AddSingleton(client);

            /*services.AddStartupTask<ClusterClientStartupTask>();

            services
                .AddHealthChecks()
                .AddCheck<StartupTasksHealthCheck>("Startup tasks");
             */
        }


        /// <summary>
        /// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        /// </summary>
        /// <param name="app">The application to configure.</param>
        /// <param name="env">The environment information to use in configuration phase.</param>
        /// <param name="applicationLifetime"></param>
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IHostApplicationLifetime applicationLifetime)
        {
            app.UseProblemDetails();
            if(!env.IsProduction())
            {
                //This test middleware needs to be before developer exception pages.
                app.Use(TestMiddleware);
                app.UseDeveloperExceptionPage();
            }

            //app.UseHealthChecks("/healthz");
            //app.UseMiddleware<StartupTasksMiddleware>();

            applicationLifetime.ApplicationStopping.Register(() => { });

            //This ensures (or improves chances) to flush log buffers before (a graceful) shutdown.
            //It appears there isn't other way (e.g. in Program) than taking a reference to the global
            //static Serilog instance.
            applicationLifetime.ApplicationStopped.Register(Log.CloseAndFlush);

            //Security headers will always be added and by default the disallow everything.
            //The trimming is a robustness measure to make sure the URL has one trailing slash.
            //The listening address is needed for security headers. This is the public
            //API address.
            var appsettingsSection = Configuration.GetSection("AppSettings");
            var listeningAddress = appsettingsSection["OneBoxDeploymentApiUrl"];
            listeningAddress = (listeningAddress ?? app.ServerFeatures.Get<IServerAddressesFeature>().Addresses.FirstOrDefault()).EnsureTrailing('/');

            //Note: the constructor checks ConfigurationKeys forbidden in production are not found.
            if(!env.IsProduction())
            {
                //Creates a route to specifically throw and unhandled exception. This route is most likely injected only in testing.
                //This kind of testing middleware can be made to one code block guarded appropriately if there is more of it.
                var alwaysFaultyRoute = Configuration.GetValue<string>(ConfigurationKeys.AlwaysFaultyRoute, null);
                if(alwaysFaultyRoute != null)
                {
                    app.Use((context, next) =>
                    {
                        if(context.Request.Path.StartsWithSegments($"/{alwaysFaultyRoute}", out _, out _))
                        {
                            throw new Exception($"Fault injected route for testing ({context.Request.PathBase}/{alwaysFaultyRoute}).");
                        }

                        return next();
                    });
                }
            }

            Logger.LogInformation(Events.SwaggerDocumentation.Id, Events.SwaggerDocumentation.FormatString, listeningAddress + SwaggerRoot + "/");
            var defaultSecurityPolicies = new HeaderPolicyCollection()
                .AddStrictTransportSecurityMaxAgeIncludeSubDomains(maxAgeInSeconds: 60 * 60 * 24 * 365)
                .RemoveServerHeader()
                .AddFrameOptionsDeny();
            app.UseSecurityHeaders(defaultSecurityPolicies);
            app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/" + SwaggerRoot), swaggerBranch =>
            {
                //See configuration at https://github.com/andrewlock/NetEscapades.AspNetCore.SecurityHeaders.
                const string GoogleStyles = "https://fonts.googleapis.com";
                const string GoogleFontsUrl = "https://fonts.gstatic.com";
                var clientUrl = Path.Combine(listeningAddress, SwaggerRoot).EnsureTrailing('/');
                //Additional information for the many Feature-Policy none definitions:
                //https://github.com/w3c/webappsec-feature-policy/issues/189#issuecomment-452401661.
                swaggerBranch.UseSecurityHeaders(new HeaderPolicyCollection().AddFeaturePolicy(builder =>
                {
                    builder.AddAccelerometer().None();
                    builder.AddAmbientLightSensor().None();
                    builder.AddAutoplay().None();
                    builder.AddCamera().None();
                    builder.AddEncryptedMedia().None();
                    builder.AddFullscreen().None();
                    builder.AddGeolocation().None();
                    builder.AddGyroscope().None();
                    builder.AddMagnetometer().None();
                    builder.AddMicrophone().None();
                    builder.AddMidi().None();
                    builder.AddPayment().None();
                    builder.AddPictureInPicture().None();
                    builder.AddSpeaker().None();
                    builder.AddSyncXHR().None();
                    builder.AddUsb().None();
                    builder.AddVR().None();
                })
                .AddXssProtectionBlock()
                .AddContentTypeOptionsNoSniff()
                .AddReferrerPolicyStrictOriginWhenCrossOrigin()
                .AddContentSecurityPolicy(builder =>
                {
                    builder.AddReportUri().To("/cspreport");
                    builder.AddBlockAllMixedContent();
                    builder.AddConnectSrc().Self();
                    builder.AddStyleSrc().Self().UnsafeInline().Sources.Add(GoogleStyles);
                    builder.AddFontSrc().Self().Sources.Add(GoogleFontsUrl);
                    builder.AddImgSrc().Self().Sources.Add("data:");
                    builder.AddScriptSrc().Self().UnsafeInline();
                    builder.AddObjectSrc().None();
                    builder.AddFormAction().Self();
                    builder.AddFrameAncestors().None().Sources.Add(clientUrl);
                }, asReportOnly: false));
            });

            //For further Swagger related information, see at
            //https://docs.microsoft.com/en-us/aspnet/core/tutorials/web-api-help-pages-using-swagger.
            app.UseSwagger();
            app.UseSwagger(swagger => swagger.RouteTemplate = $"{SwaggerRoot}/{{documentName}}/swagger.json");

            if(Configuration["HideSwaggerUi"]?.Equals("true") != true)
            {
                app.UseSwaggerUI(swaggerSetup =>
                {
                    swaggerSetup.SwaggerEndpoint($"/{SwaggerRoot}/{SwaggerDocumentationBasePath}/swagger.json", SwaggerDocumentationBasePath);
                    swaggerSetup.RoutePrefix = SwaggerRoot;

                    swaggerSetup.IndexStream = () => GetType().GetTypeInfo().Assembly.GetManifestResourceStream($"{Assembly.GetAssembly(typeof(Startup)).GetName().Name}.wwwroot.swagger.index.html");
                });
            }

            app.UseCors("CorsPolicy");
            app.UseStaticFiles();
            app.UseRouting();
            //app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }


        private void ConfigureProblemDetails(ProblemDetailsOptions options)
        {
            // This is the default behavior; only include exception details in a development environment.
            options.IncludeExceptionDetails = (ctx, ex) => Environment.IsDevelopment();

            options.OnBeforeWriteDetails = (ctx, details) => details.Instance = $"urn:oneboxdeployment:error:{ctx.TraceIdentifier}";

            // You can configure the middleware to re-throw certain types of exceptions, all exceptions or based on a predicate.
            // This is useful if you have upstream middleware that needs to do additional handling of exceptions.
            options.Rethrow<NotSupportedException>();

            // This will map NotImplementedException to the 501 Not Implemented status code.
            options.MapToStatusCode<NotImplementedException>(StatusCodes.Status501NotImplemented);

            // This will map HttpRequestException to the 503 Service Unavailable status code.
            options.MapToStatusCode<HttpRequestException>(StatusCodes.Status503ServiceUnavailable);

            // Because exceptions are handled polymorphically, this will act as a "catch all" mapping, which is why it's added last.
            // If an exception other than NotImplementedException and HttpRequestException is thrown, this will handle it.
            options.MapToStatusCode<Exception>(StatusCodes.Status500InternalServerError);
        }


        /// <summary>
        /// This middleware should be used only when testing.
        /// </summary>
        /// <param name="context">The call context.</param>
        /// <param name="next">Function to call next middlware in the pipeline.</param>
        private Task TestMiddleware(HttpContext context, Func<Task> next)
        {
            //Creates a route to specifically throw and unhandled exception. This route is most likely injected only in testing.
            var alwaysFaultyRoute = Configuration.GetValue<string>(ConfigurationKeys.AlwaysFaultyRoute, null);
            if(alwaysFaultyRoute != null && context.Request.Path.StartsWithSegments(alwaysFaultyRoute, out _, out _))
            {
                throw new Exception("This is an exception thrown from middleware.");
            }

            return next();
        }
    }
}
