using System.Security.Cryptography;
using HomeLabControl.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomeLabControl.Services;

/// <summary>
/// Пользователи в config/users.yaml (рядом с HomeLabControl.yaml). Пароли — PBKDF2-SHA256.
/// </summary>
public class UserService
{
    private const int Iterations = 210_000;

    private readonly string _path;
    private readonly ILogger<UserService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly object _lock = new();
    private UsersConfig _config = new();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitEmptyCollections | DefaultValuesHandling.OmitNull)
        .WithIndentedSequences()
        .Build();

    public UserService(HlcConfigService configService, IConfiguration configuration, ILogger<UserService> logger)
    {
        _logger = logger;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configService.ConfigPath)) ?? ".";
        _path = Path.Combine(configDir, "users.yaml");
        Load();
        EnsureEnvAdmin(configuration["HLC_ADMIN_USER"], configuration["HLC_ADMIN_PASSWORD"]);
    }

    public string FilePath => _path;

    /// <summary>
    /// Администратор из переменных окружения HLC_ADMIN_USER / HLC_ADMIN_PASSWORD (null — не задан).
    /// Его пароль управляется только через env: в UI его нельзя сменить или удалить учётку.
    /// </summary>
    public string? EnvAdminName { get; private set; }

    private const string EnvManagedMessage =
        "This administrator is managed by HLC_ADMIN_USER / HLC_ADMIN_PASSWORD (docker-compose): change the password there and restart";

    public bool IsEnvManaged(string? name)
        => EnvAdminName != null && name != null && name.Equals(EnvAdminName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Фоллбэк-администратор из docker-compose. При старте: нет пользователя — создаётся;
    /// пароль не совпадает / не админ / отключён — исправляется (так восстанавливается доступ).
    /// Если всё совпадает — файл не трогаем, чтобы не сбрасывать сессии при каждом перезапуске.
    /// </summary>
    private void EnsureEnvAdmin(string? name, string? password)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrEmpty(password))
            return;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password) || password.Length < 8)
        {
            _logger.LogError("HLC_ADMIN_USER / HLC_ADMIN_PASSWORD: both must be set, password at least 8 characters — ignored");
            return;
        }

        name = name.Trim();
        EnvAdminName = name;

        var existing = Find(name);
        if (existing != null && existing.Admin && !existing.Disabled && VerifyPassword(password, existing.PasswordHash))
            return;

        UpdateAsync(config =>
        {
            var user = config.Users.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (user == null)
            {
                config.Users.Add(new HlcUser { Name = name, PasswordHash = HashPassword(password), Admin = true });
                return;
            }

            user.Admin = true;
            user.Disabled = false;
            user.PasswordHash = HashPassword(password);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }).GetAwaiter().GetResult();

        _logger.LogWarning(existing == null
            ? "Administrator '{User}' created from HLC_ADMIN_USER / HLC_ADMIN_PASSWORD"
            : "Administrator '{User}' restored from HLC_ADMIN_USER / HLC_ADMIN_PASSWORD (password / admin / enabled reset)", name);
    }

    public bool HasUsers
    {
        get { lock (_lock) return _config.Users.Count > 0; }
    }

    public List<HlcUser> GetUsers()
    {
        lock (_lock) return _config.Users.Select(Clone).ToList();
    }

    public HlcUser? Find(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        lock (_lock)
        {
            var user = _config.Users.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return user == null ? null : Clone(user);
        }
    }

    /// <summary>Проверка логина/пароля. Время ответа не зависит от того, существует ли пользователь.</summary>
    public HlcUser? Validate(string name, string password)
    {
        var user = Find(name);
        var ok = VerifyPassword(password, user?.PasswordHash ?? DummyHash);
        return ok && user is { Disabled: false } ? user : null;
    }

    // ─── Изменение ────────────────────────────────────────────────────────────

    /// <summary>Первый администратор (страница /setup). Возвращает false, если пользователи уже есть.</summary>
    public async Task<bool> CreateFirstAdminAsync(string name, string password)
    {
        await _saveLock.WaitAsync();
        try
        {
            lock (_lock)
            {
                if (_config.Users.Count > 0)
                    return false;
            }

            var config = new UsersConfig();
            config.Users.Add(new HlcUser { Name = name.Trim(), PasswordHash = HashPassword(password), Admin = true });
            await SaveAsync(config);
            _logger.LogWarning("First administrator '{User}' created", name);
            return true;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public Task AddAsync(HlcUser user, string password)
        => UpdateAsync(config =>
        {
            ValidateName(user.Name);
            if (config.Users.Any(u => u.Name.Equals(user.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"User '{user.Name}' already exists");
            ValidatePassword(password);

            config.Users.Add(new HlcUser
            {
                Name = user.Name.Trim(),
                PasswordHash = HashPassword(password),
                Admin = user.Admin,
                Permissions = NormalizePermissions(user.Permissions),
                Hosts = NormalizeHosts(user.Hosts),
                Disabled = user.Disabled
            });
        });

    /// <summary>Права, флаг admin и отключение. Сбрасывает сессии пользователя.</summary>
    public Task UpdateAccessAsync(string name, bool admin, Dictionary<string, string> permissions, List<string> hosts, bool disabled)
        => UpdateAsync(config =>
        {
            if (IsEnvManaged(name) && (!admin || disabled))
                throw new InvalidOperationException(EnvManagedMessage);

            var user = Get(config, name);
            user.Admin = admin;
            user.Permissions = NormalizePermissions(permissions);
            user.Hosts = NormalizeHosts(hosts);
            user.Disabled = disabled;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            EnsureActiveAdmin(config);
        });

    public Task SetPasswordAsync(string name, string password)
        => UpdateAsync(config =>
        {
            if (IsEnvManaged(name))
                throw new InvalidOperationException(EnvManagedMessage);

            ValidatePassword(password);
            var user = Get(config, name);
            user.PasswordHash = HashPassword(password);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        });

    public Task DeleteAsync(string name)
        => UpdateAsync(config =>
        {
            if (IsEnvManaged(name))
                throw new InvalidOperationException(EnvManagedMessage);

            config.Users.Remove(Get(config, name));
            EnsureActiveAdmin(config);
        });

    private async Task UpdateAsync(Action<UsersConfig> mutate)
    {
        await _saveLock.WaitAsync();
        try
        {
            UsersConfig copy;
            lock (_lock)
                copy = new UsersConfig { Users = _config.Users.Select(Clone).ToList() };

            mutate(copy);
            await SaveAsync(copy);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static HlcUser Get(UsersConfig config, string name)
        => config.Users.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"User '{name}' not found");

    /// <summary>Нельзя остаться без активного администратора — иначе в Config больше не попасть.</summary>
    private static void EnsureActiveAdmin(UsersConfig config)
    {
        if (!config.Users.Any(u => u.Admin && !u.Disabled))
            throw new InvalidOperationException("At least one active administrator is required");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64 || name.Any(char.IsControl))
            throw new ArgumentException("Invalid user name");
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters");
    }

    private static List<string> NormalizeHosts(List<string>? hosts)
        => (hosts ?? new()).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static Dictionary<string, string> NormalizePermissions(Dictionary<string, string> permissions)
        => permissions
            .Where(p => HlcModules.All.Contains(p.Key) && (p.Value == HlcModules.View || (p.Value == HlcModules.Control && HlcModules.HasControl(p.Key))))
            .ToDictionary(p => p.Key, p => p.Value);

    // ─── Файл ─────────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogWarning("No users file {Path} — open the UI to create the first administrator", _path);
                return;
            }

            var config = Deserializer.Deserialize<UsersConfig>(File.ReadAllText(_path)) ?? new UsersConfig();
            lock (_lock) _config = config;
            _logger.LogInformation("Loaded {Count} users from {Path}", config.Users.Count, _path);
        }
        catch (Exception ex)
        {
            // Битый users.yaml не должен открывать UI: пользователей "нет" → /setup, но создание
            // первого админа перезаписало бы файл. Поэтому падаем громко.
            _logger.LogCritical(ex, "Failed to read {Path}", _path);
            throw;
        }
    }

    private async Task SaveAsync(UsersConfig config)
    {
        var yaml = "# Пользователи HomeLabControl. Управляются на странице Config → Users.\n" + Serializer.Serialize(config);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, yaml);
        File.Move(temp, _path, overwrite: true);

        lock (_lock) _config = config;
    }

    private static HlcUser Clone(HlcUser u) => new()
    {
        Name = u.Name,
        PasswordHash = u.PasswordHash,
        Admin = u.Admin,
        Permissions = new Dictionary<string, string>(u.Permissions),
        Hosts = u.Hosts.ToList(),
        Disabled = u.Disabled,
        SecurityStamp = u.SecurityStamp
    };

    // ─── Пароли ───────────────────────────────────────────────────────────────

    private static readonly string DummyHash = HashPassword(Guid.NewGuid().ToString());

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
