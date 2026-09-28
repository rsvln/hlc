// Program.cs
using HomeLabControlAgent.Auth;
using HomeLabControlAgent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace HomeLabControlAgent;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Contains("--generate-key"))
        {
            Console.WriteLine(ApiKeyAuthenticationHandler.GenerateKey());
            return;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // Служба стартует с cwd = C:\Windows\System32 — конфиги ищем рядом с exe
            ContentRootPath = AppContext.BaseDirectory
        });

        // Локальные настройки машины (ключи API и т.п.) — не входят в publish и не перезатираются деплоем
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables();

        // Конфигурация для работы как служба
        if (OperatingSystem.IsWindows())
        {
            builder.Host.UseWindowsService();
        }
        else if (OperatingSystem.IsLinux())
        {
            builder.Host.UseSystemd();
        }

        // Порт из ServicePort, если адреса не заданы явно через Urls / Kestrel
        if (string.IsNullOrEmpty(builder.Configuration["Urls"]) && !builder.Configuration.GetSection("Kestrel").Exists())
        {
            var servicePort = builder.Configuration.GetValue("ServicePort", 8117);
            builder.WebHost.UseUrls($"http://0.0.0.0:{servicePort}");
        }

        // Аутентификация по API-ключу: все эндпоинты закрыты, кроме помеченных [AllowAnonymous]
        builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection(ApiKeyOptions.SectionName));
        builder.Services
            .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        // Регистрация сервисов
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "HomeLab Control Agent API",
                Version = "v1",
                Description = "Sensors, fans and fan profiles, SMART and power control of this machine"
            });

            options.AddSecurityDefinition(ApiKeyAuthenticationHandler.SchemeName, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyAuthenticationHandler.HeaderName,
                Description = "Agent API key (Auth:ApiKeys in appsettings.Local.json)"
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.SchemeName, document)] = []
            });
        });

        // Platform-specific сервисы
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<IHardwareMonitor, WindowsHardwareMonitor>();
            builder.Services.AddSingleton<IPowerService, WindowsPowerService>();
        }
        else if (OperatingSystem.IsLinux())
        {
            builder.Services.AddSingleton<IHardwareMonitor, LinuxHardwareMonitor>();
            builder.Services.AddSingleton<IPowerService, LinuxPowerService>();
        }
        else
        {
            throw new PlatformNotSupportedException("Only Windows and Linux are supported");
        }

        // Fan Control сервисы
        builder.Services.AddSingleton<IProfileStorage, ProfileStorage>();
        builder.Services.AddSingleton<CurveCalculator>();
        builder.Services.AddHostedService<ProfileEngine>();
        builder.Services.AddSingleton<ISmartMonitorService, SmartMonitorService>();

        // Логирование
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        if (OperatingSystem.IsWindows())
        {
            builder.Logging.AddEventLog();
        }

        var app = builder.Build();

        app.Logger.LogInformation("HomeLabControlAgent v{Version}", VersionInfo.Full);
        WarnAboutAuthConfiguration(app);

        // Middleware
        if (app.Configuration.GetValue("Swagger:Enabled", true))
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "HomeLab Control Agent API v1");
                c.RoutePrefix = string.Empty;
            });
        }

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        app.Run();
    }

    private static void WarnAboutAuthConfiguration(WebApplication app)
    {
        var auth = app.Configuration.GetSection(ApiKeyOptions.SectionName).Get<ApiKeyOptions>() ?? new ApiKeyOptions();

        if (!auth.Enabled)
        {
            app.Logger.LogWarning("API authentication is DISABLED (Auth:Enabled=false) — anyone in the network can control this host");
        }
        else if (!auth.ApiKeys.Values.Any(k => !string.IsNullOrWhiteSpace(k)))
        {
            app.Logger.LogWarning(
                "No API keys configured — API is OPEN to anyone in the network. " +
                "Generate a key with '--generate-key' and put it into appsettings.Local.json: " +
                "{{ \"Auth\": {{ \"ApiKeys\": {{ \"homelabcontrol\": \"<key>\" }} }} }}");
        }
        else
        {
            app.Logger.LogInformation("API key authentication enabled for clients: {Clients}",
                string.Join(", ", auth.ApiKeys.Where(k => !string.IsNullOrWhiteSpace(k.Value)).Select(k => k.Key)));
        }
    }
}
