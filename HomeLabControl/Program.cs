using HomeLabControl.Auth;
using HomeLabControl.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace HomeLabControl
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    // .NET 10: blazor.server.js — static web asset. В publish он лежит в wwwroot,
                    // а при запуске из bin (не Development) без этого вызова отдаётся 404
                    webBuilder.UseStaticWebAssets();
                    webBuilder.UseStartup<Startup>();
                });
    }

    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            // Ключи шифрования cookie — в томе с конфигом, иначе после пересоздания контейнера все сессии слетают
            var configDir = Path.GetDirectoryName(Path.GetFullPath(
                (Configuration["AppConfigPath"] ?? "config/HomeLabControl.yaml").Trim())) ?? "config";
            services.AddDataProtection()
                .SetApplicationName("HomeLabControl")
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configDir, "keys")));

            // Аутентификация: cookie, страница входа /login, пользователи в config/users.yaml
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = "hlc_auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.LoginPath = "/login";
                    options.AccessDeniedPath = "/";
                    options.ExpireTimeSpan = TimeSpan.FromDays(30);
                    options.SlidingExpiration = true;
                    options.Events.OnValidatePrincipal = CookieValidation.ValidateAsync;

                    // REST API: 401/403 вместо редиректа на страницу входа
                    options.Events.OnRedirectToLogin = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        else
                            ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    };
                    options.Events.OnRedirectToAccessDenied = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        else
                            ctx.Response.Redirect(ctx.RedirectUri);
                        return Task.CompletedTask;
                    };
                });

            // За reverse-proxy (HLC_BEHIND_PROXY=true): реальный IP клиента и схема из X-Forwarded-For / -Proto —
            // для журнала, защиты от перебора и Secure-cookie при HTTPS на прокси
            if (Configuration.GetValue("HLC_BEHIND_PROXY", false))
            {
                services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
                {
                    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto |
                                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
                    // Прокси обычно в той же сети/на том же хосте (Docker) — доверяем любому; HLC не должен быть доступен в обход прокси
                    options.KnownIPNetworks.Clear();
                    options.KnownProxies.Clear();
                });
            }

            services.AddAuthorization(Policies.Register);
            services.AddSingleton<IAuthorizationHandler, HlcPermissionHandler>();
            services.AddSingleton<UserService>();
            services.AddSingleton<LoginThrottle>();
            services.AddSingleton<AuditService>();
            services.AddScoped<HlcAuth>();
            services.AddCascadingAuthenticationState();

            // Blazor
            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddControllers();

            // HttpClient factory
            services.AddHttpClient();

            // Core services
            services.AddSingleton<HlcConfigService>();

            // Module services
            services.AddSingleton<PowerControlService>();
            services.AddSingleton<FanControlService>();
            services.AddSingleton<BackupHistoryService>();
            services.AddSingleton<BackupManagerService>();
            services.AddSingleton<BackupSchedulerService>();
            services.AddHostedService(sp => sp.GetRequiredService<BackupSchedulerService>());
            services.AddSingleton<SmartMonitorService>();

            // Агенты: здоровье/версии, деплой/обновление/удаление
            services.AddSingleton<AgentStatusService>();
            services.AddSingleton<DeployService>();

            // Мониторинг агентов, уведомления, MQTT / Home Assistant
            services.AddSingleton<AgentMonitorService>();
            services.AddHostedService(sp => sp.GetRequiredService<AgentMonitorService>());
            services.AddSingleton<MqttService>();
            services.AddHostedService(sp => sp.GetRequiredService<MqttService>());
            services.AddSingleton<NotificationService>();
            services.AddHostedService(sp => sp.GetRequiredService<NotificationService>());
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (!env.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            // app.UseHttpsRedirection(); // Commented out
            // Статика без отпечатков в URL (_framework, css, js): без Cache-Control браузер эвристически
            // держит старую версию после обновления. no-cache = всегда перепроверять, по ETag ответ 304.
            if (Configuration.GetValue("HLC_BEHIND_PROXY", false))
                app.UseForwardedHeaders();

            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
            });
            app.UseRouting();

            // Пока нет ни одного пользователя — всё ведёт на создание администратора
            app.Use(async (context, next) =>
            {
                var users = context.RequestServices.GetRequiredService<UserService>();
                var path = context.Request.Path;
                if (!users.HasUsers && !path.StartsWithSegments("/setup"))
                {
                    if (path.StartsWithSegments("/api") || path.StartsWithSegments("/_blazor"))
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    else
                        context.Response.Redirect("/setup");
                    return;
                }
                await next();
            });

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
                endpoints.MapControllers();
            });
        }
    }
}
