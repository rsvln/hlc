// Auth/ApiKeyAuthentication.cs
namespace HomeLabControlAgent.Auth;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

/// <summary>
/// Настройки секции "Auth" в конфигурации.
/// Ключи лучше хранить в appsettings.Local.json (не попадает в publish/деплой)
/// или в переменных окружения: Auth__ApiKeys__ha=...
/// </summary>
public class ApiKeyOptions
{
    public const string SectionName = "Auth";

    /// <summary>false — аутентификация выключена полностью (не рекомендуется).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Имя клиента → ключ. Имя попадает в логи, сам ключ — никогда.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();
}

/// <summary>
/// Принимает ключ из заголовка "X-Api-Key: &lt;key&gt;" или "Authorization: Bearer &lt;key&gt;".
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly IOptionsMonitor<ApiKeyOptions> _apiKeyOptions;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<ApiKeyOptions> apiKeyOptions)
        : base(options, logger, encoder)
    {
        _apiKeyOptions = apiKeyOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var settings = _apiKeyOptions.CurrentValue;

        // Выключено явно или ключи ещё не настроены (свежая установка/обновление) —
        // пропускаем всех, чтобы не сломать существующие интеграции; при старте пишется warning
        if (!settings.Enabled || !settings.ApiKeys.Values.Any(k => !string.IsNullOrWhiteSpace(k)))
            return Task.FromResult(Success("anonymous"));

        var presented = ExtractKey();
        if (string.IsNullOrEmpty(presented))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        foreach (var (client, key) in settings.ApiKeys)
        {
            if (string.IsNullOrEmpty(key))
                continue;

            if (CryptographicOperations.FixedTimeEquals(presentedBytes, Encoding.UTF8.GetBytes(key)))
                return Task.FromResult(Success(client));
        }

        Logger.LogWarning("Rejected request with invalid API key from {RemoteIp} to {Path}",
            Context.Connection.RemoteIpAddress, Request.Path);
        return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Response.WriteAsJsonAsync(new { error = "API key required: X-Api-Key or Authorization: Bearer <key>" });
    }

    private string? ExtractKey()
    {
        if (Request.Headers.TryGetValue(HeaderName, out var headerValue) && !string.IsNullOrWhiteSpace(headerValue))
            return headerValue.ToString().Trim();

        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();

        return null;
    }

    private AuthenticateResult Success(string client)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, client) }, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>Криптостойкий ключ для --generate-key.</summary>
    public static string GenerateKey()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
