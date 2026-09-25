// Services/ProfileStorage.cs
namespace HomeLabControlAgent.Services;

using HomeLabControlAgent.Models;
using System.Text.Json;

public interface IProfileStorage
{
    Task<List<FanProfile>> GetAllAsync();
    Task<FanProfile?> GetByIdAsync(string id);
    Task SaveAsync(FanProfile profile);
    Task DeleteAsync(string id);
}

/// <summary>
/// Профили в JSON-файле. Файл читается один раз при старте, дальше — работа с копией в памяти
/// (ProfileEngine обращается к хранилищу каждые 500 мс). Запись атомарная: temp-файл + замена.
/// </summary>
public class ProfileStorage : IProfileStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<ProfileStorage> _logger;
    private List<FanProfile>? _cache;

    public ProfileStorage(IConfiguration config, ILogger<ProfileStorage> logger)
    {
        var fileName = config.GetValue<string>("ProfilesFilePath") ?? "profiles.json";

        // Относительный путь — относительно каталога приложения, а не текущего каталога процесса
        _filePath = Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(AppContext.BaseDirectory, fileName);

        _logger = logger;
        _logger.LogInformation("ProfileStorage initialized with path: {Path}", _filePath);
    }

    public async Task<List<FanProfile>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return Clone(EnsureLoaded());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FanProfile?> GetByIdAsync(string id)
    {
        var profiles = await GetAllAsync();
        return profiles.FirstOrDefault(p => p.Id == id);
    }

    public async Task SaveAsync(FanProfile profile)
    {
        await _lock.WaitAsync();
        try
        {
            var profiles = Clone(EnsureLoaded());
            var index = profiles.FindIndex(p => p.Id == profile.Id);

            if (index >= 0)
                profiles[index] = profile;
            else
                profiles.Add(profile);

            await PersistAsync(profiles);
            _logger.LogInformation("Saved profile {ProfileId}", profile.Id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var profiles = Clone(EnsureLoaded());
            profiles.RemoveAll(p => p.Id == id);

            await PersistAsync(profiles);
            _logger.LogInformation("Deleted profile {ProfileId}", id);
        }
        finally
        {
            _lock.Release();
        }
    }

    private List<FanProfile> EnsureLoaded()
    {
        if (_cache != null)
            return _cache;

        try
        {
            _cache = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<FanProfile>>(File.ReadAllText(_filePath)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            // Не затираем повреждённый файл пустым списком при следующем сохранении — сохраняем копию
            _logger.LogError(ex, "Failed to read profiles from {Path}", _filePath);
            TryBackupCorruptedFile();
            _cache = new();
        }

        return _cache;
    }

    private async Task PersistAsync(List<FanProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles, JsonOptions);
        var tempPath = _filePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);

        _cache = profiles;
    }

    private void TryBackupCorruptedFile()
    {
        try
        {
            var backup = $"{_filePath}.corrupted-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(_filePath, backup, overwrite: true);
            _logger.LogWarning("Corrupted profiles file copied to {Backup}", backup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to back up corrupted profiles file");
        }
    }

    // Отдаём копии, чтобы вызывающий код не мутировал кэш (контроллеры меняют Enabled и т.п.)
    private static List<FanProfile> Clone(List<FanProfile> profiles)
        => JsonSerializer.Deserialize<List<FanProfile>>(JsonSerializer.Serialize(profiles))!;
}
