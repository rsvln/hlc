namespace HomeLabControl.Models;

/// <summary>Пользователи HomeLabControl (config/users.yaml, управляется на странице Config → Users).</summary>
public class UsersConfig
{
    public List<HlcUser> Users { get; set; } = new();
}

public class HlcUser
{
    public string Name { get; set; } = "";

    /// <summary>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt b64&gt;$&lt;hash b64&gt;</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Администратор: всё, включая Config, агентов (деплой/обновление/удаление) и пользователей.</summary>
    public bool Admin { get; set; }

    /// <summary>Права по модулям: power / fanControl / smart / backup → view | control.</summary>
    public Dictionary<string, string> Permissions { get; set; } = new();

    /// <summary>Хосты (name из HomeLabControl.yaml), с которыми пользователь может работать. Пусто — все хосты.</summary>
    public List<string> Hosts { get; set; } = new();

    public bool Disabled { get; set; }

    /// <summary>Меняется при смене пароля, прав, отключении — старые сессии пользователя сбрасываются.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>Модули и уровни доступа.</summary>
public static class HlcModules
{
    public const string Power = "power";
    public const string FanControl = "fanControl";
    public const string Smart = "smart";
    public const string Backup = "backup";

    public static readonly string[] All = { Power, FanControl, Smart, Backup };

    /// <summary>SMART только на просмотр — управлять там нечем (refresh считается просмотром).</summary>
    public static bool HasControl(string module) => module != Smart;

    public const string View = "view";
    public const string Control = "control";
}
