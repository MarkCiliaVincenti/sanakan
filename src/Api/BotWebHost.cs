#pragma warning disable 1591

using Microsoft.AspNetCore.Hosting;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.HttpOverrides;
using System.IO;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Logging;
using System.Text;
using Sanakan.Config;
using Sanakan.Services.Executor;
using Discord.WebSocket;
using Shinden;
using Sanakan.Services.PocketWaifu;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Microsoft.OpenApi.Models;
using Sanakan.Services.Time;

namespace Sanakan.Api
{
    public static class BotWebHost
    {
        public static void RunWebHost(DiscordSocketClient client, ShindenClient shinden, Waifu waifu, IConfig config, Services.Helper helper,
            IExecutor executor, Shinden.Logger.ILogger logger, ISystemTime time)
        {
            new Thread(() =>
            {
                Thread.CurrentThread.IsBackground = true;
                CreateWebHostBuilder(config).ConfigureServices(services =>
                {
                    services.AddSingleton(time);
                    services.AddSingleton(waifu);
                    services.AddSingleton(logger);
                    services.AddSingleton(client);
                    services.AddSingleton(helper);
                    services.AddSingleton(shinden);
                    services.AddSingleton(executor);
                }).Build().Run();
            }).Start();
        }

        private static IWebHostBuilder CreateWebHostBuilder(IConfig config) =>
            WebHost.CreateDefaultBuilder().ConfigureServices(services =>
            {
                var tmpCnf = config.Get();
                services.AddMemoryCache();
                services.AddSingleton(config);
                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(opt =>
                {
                    opt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = tmpCnf.Jwt.Issuer,
                        ValidAudience = tmpCnf.Jwt.Issuer,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tmpCnf.Jwt.Key))
                    };
                });
                services.AddAuthorization(op =>
                {
                    op.AddPolicy("Player", policy =>
                    {
                        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                        policy.RequireAuthenticatedUser();

                        policy.RequireAssertion(context => context.User.HasClaim(c => c.Type == "Player" && c.Value == "waifu_player"));
                    });

                    op.AddPolicy("Site", policy =>
                    {
                        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                        policy.RequireAuthenticatedUser();

                        policy.RequireAssertion(context => !context.User.HasClaim(c => c.Type == "Player"));
                    });
                });
                services.AddControllers()
                    .AddNewtonsoftJson(o => o.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore)
                    .AddNewtonsoftJson(o => o.SerializerSettings.Converters.Add(new StringEnumConverter { NamingStrategy = new CamelCaseNamingStrategy() }));
                services.AddCors(options =>
                {
                    options.AddPolicy("AllowEverything", builder =>
                    {
                        builder.AllowAnyOrigin();
                        builder.AllowAnyHeader();
                        builder.AllowAnyMethod();
                    });
                });
                services.AddApiVersioning(o =>
                {
                    o.AssumeDefaultVersionWhenUnspecified = true;
                    o.DefaultApiVersion = new Asp.Versioning.ApiVersion(1, 0);
                    o.ApiVersionReader = new Asp.Versioning.HeaderApiVersionReader("x-api-version");
                });
                services.AddSwaggerGen(c =>
                {
                    c.SwaggerDoc("v2", new OpenApiInfo
                    {
                        Title = "Sanakan API",
                        Version = "1.0",
                        Description = "Autentykacja następuje poprzez dopasowanie tokenu przesłanego w ciele zapytania `api/token`, a następnie wysyłania w nagłowku `Authorization` z przedrostkiem `Bearer` otrzymanego w zwrocie tokena."
                            + "\n\nDocelowa wersja api powinna zostać przesłana pod nagówkiem `x-api-version`, w przypadku jej nie podania zapytania są interpretowane jako wysłane do wersji `1.0`.",
                    });

                    var filePath = Path.Combine(System.AppContext.BaseDirectory, "Sanakan.xml");
                    if (File.Exists(filePath)) c.IncludeXmlComments(filePath);

                    c.CustomSchemaIds(x => x.FullName);
                });
            }).ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSimpleConsole(x => x.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled);
                logging.SetMinimumLevel(LogLevel.Warning);
            })
            .Configure(app =>
            {
                app.UseSwagger();
                app.UseCors("AllowEverything");
                app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
                app.UseStaticFiles();
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                });
#if !DEBUG
            });
#else
            }).UseUrls("http://*:5005");
#endif
            }
}