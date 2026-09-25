using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeLabControl.Models;
using Host = HomeLabControl.Models.Host;
using Renci.SshNet;
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace HomeLabControl.Services;

/// <summary>Параметры деплоя/обновления агента.</summary>
public class AgentDeployRequest
{
    public string HostName { get; set; } = "";
    public string Ip { get; set; } = "";
    public int AgentPort { get; set; } = 8117;

    /// <summary>linux / windows</summary>
    public string Os { get; set; } = "linux";

    public string SshUser { get; set; } = "root";
    public int SshPort { get; set; } = 22;

    /// <summary>Пароль SSH — нужен только пока на хосте нет SSH-ключа HomeLabControl (первый деплой).</summary>
    public string? SshPassword { get; set; }

    public string ServiceName { get; set; } = DeployService.DefaultServiceName;
    public string InstallPath { get; set; } = "";

    /// <summary>Каталог со сборкой агента; пусто — сборка из образа (/app/agent/&lt;rid&gt;) или deploy.*.sourcePath.</summary>
    public string? SourcePath { get; set; }
}

public class DeployResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Log { get; set; } = new();

    /// <summary>Ключ Home Assistant для хоста (показывается в UI).</summary>
    public string? HaApiKey { get; set; }

    /// <summary>true — ключ HA сгенерирован в этот раз: rest_command в HA нужно обновить.</summary>
    public bool HaKeyIsNew { get; set; }
}

/// <summary>
/// Деплой, обновление и удаление HomeLabControlAgent по SSH/SFTP.
/// Сборка заливается одним tar.gz и распаковывается на хосте (tar есть в Linux и Windows 10+).
/// </summary>
public class DeployService
{
    public const string AgentModule = "homeLabControlAgent";
    public const string DefaultServiceName = "homeLabControlAgent";

    private const string AgentLocalSettingsFile = "appsettings.Local.json";
    private const string PackageFileName = "_hlca_package.tar.gz";
    private const string HlcClientName = "homelabcontrol";
    private const string HaClientName = "homeassistant";

    /// <summary>Локальное состояние агента — не входит в пакет, чтобы деплой его не затирал.</summary>
    private static readonly string[] PreservedFiles = { "profiles.json", AgentLocalSettingsFile };

    private readonly ILogger<DeployService> _logger;
    private readonly HlcConfigService _configService;
    private readonly AgentStatusService _statusService;

    public DeployService(ILogger<DeployService> logger, HlcConfigService configService, AgentStatusService statusService)
    {
        _logger = logger;
        _configService = configService;
        _statusService = statusService;
    }

    // ─── Пути ─────────────────────────────────────────────────────────────────

    public static string Rid(string os) => IsWindows(os) ? "win-x64" : "linux-x64";

    private static bool IsWindows(string os) => os.Equals("windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сборка агента: вшитая в образ /app/agent/&lt;rid&gt;, иначе deploy.&lt;os&gt;.homeLabControlAgent.sourcePath.</summary>
    public string GetDefaultSourcePath(string os)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "agent", Rid(os));
        return Directory.Exists(bundled) ? bundled : _configService.GetDeploySourcePath(AgentModule, os);
    }

    public string GetDefaultInstallPath(string os)
    {
        var configured = _configService.GetDeployInstallPath(AgentModule, os);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return IsWindows(os) ? @"C:\apps\homeLabControlAgent" : "/srv/homeLabControlAgent";
    }

    private string GetExecutableName(string os)
    {
        var configured = _configService.GetDeployExecutableName(AgentModule, os);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        return IsWindows(os) ? "HomeLabControlAgent.exe" : "HomeLabControlAgent";
    }

    /// <summary>Версия сборки агента, которую развернёт деплой (из HomeLabControlAgent.dll).</summary>
    public string? GetAvailableVersion(string os = "linux")
    {
        try
        {
            var dll = Path.Combine(GetDefaultSourcePath(os), "HomeLabControlAgent.dll");
            if (!File.Exists(dll))
                return null;

            var info = FileVersionInfo.GetVersionInfo(dll).ProductVersion;
            return info?.Split('+')[0];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read available agent version");
            return null;
        }
    }

    // ─── SSH-ключ HomeLabControl ──────────────────────────────────────────────

    private string KeyPath => _configService.GetHlcConfig().Deploy.SshKeyPath;

    /// <summary>Публичный ключ HLC (создаётся при первом обращении).</summary>
    public string? GetPublicKey()
    {
        try
        {
            EnsureSshKey();
            return File.ReadAllText(KeyPath + ".pub").Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HLC SSH key is not available");
            return null;
        }
    }

    private void EnsureSshKey()
    {
        if (File.Exists(KeyPath) && File.Exists(KeyPath + ".pub"))
            return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(KeyPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            ArgumentList = { "-t", "ed25519", "-N", "", "-C", "homelabcontrol-deploy", "-f", KeyPath, "-q" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start ssh-keygen");

        process.WaitForExit(15_000);
        if (process.ExitCode != 0 || !File.Exists(KeyPath))
            throw new InvalidOperationException($"ssh-keygen failed: {process.StandardError.ReadToEnd()}");

        _logger.LogInformation("Generated HLC deploy SSH key {Path}", KeyPath);
    }

    private SshConnectionInfo CreateConnectionInfo(string ip, int port, string user, string? password)
    {
        var methods = new List<AuthenticationMethod>();

        if (File.Exists(KeyPath))
            methods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(KeyPath)));

        if (!string.IsNullOrEmpty(password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));

            var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                        prompt.Response = password;
                }
            };
            methods.Add(keyboard);
        }

        if (methods.Count == 0)
            throw new InvalidOperationException("No SSH credentials: HLC SSH key is missing and no password given");

        return new SshConnectionInfo(ip, port, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    // ─── Deploy / Update ──────────────────────────────────────────────────────

    public async Task<DeployResult> DeployAsync(AgentDeployRequest req)
    {
        var result = new DeployResult();
        var windows = IsWindows(req.Os);

        try
        {
            var sourcePath = string.IsNullOrWhiteSpace(req.SourcePath) ? GetDefaultSourcePath(req.Os) : req.SourcePath;
            if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
                throw new InvalidOperationException($"Agent build not found: {sourcePath}");

            var installPath = string.IsNullOrWhiteSpace(req.InstallPath) ? GetDefaultInstallPath(req.Os) : req.InstallPath.Trim();
            var serviceName = string.IsNullOrWhiteSpace(req.ServiceName) ? DefaultServiceName : req.ServiceName.Trim();
            var executableName = GetExecutableName(req.Os);

            result.Log.Add($"Source: {sourcePath} (agent v{GetAvailableVersion(req.Os) ?? "?"})");
            result.Log.Add($"Target: {req.SshUser}@{req.Ip}:{req.SshPort} → {installPath} ({req.Os})");

            EnsureSshKey();
            var connection = CreateConnectionInfo(req.Ip, req.SshPort, req.SshUser, req.SshPassword);

            using var ssh = new SshClient(connection);
            await Task.Run(() => ssh.Connect());
            result.Log.Add("SSH connected");

            // 1. Ключ HLC на хост — дальше обновления без пароля
            InstallHlcPublicKey(ssh, windows, result);

            // 2. Остановить службу (файлы заняты процессом)
            if (windows)
            {
                RunPowerShell(ssh, $"Stop-Service -Name '{serviceName}' -Force -ErrorAction SilentlyContinue; " +
                                   "Get-Process ShutdownDialog -ErrorAction SilentlyContinue | Stop-Process -Force", result);
                RunPowerShell(ssh, $"New-Item -ItemType Directory -Path '{installPath}' -Force | Out-Null", result);
            }
            else
            {
                Run(ssh, $"systemctl stop {serviceName} 2>/dev/null || true", result);
                Run(ssh, $"mkdir -p '{installPath}'", result);
            }

            // 3. Пакет: один архив вместо сотен файлов по SFTP
            var packagePath = CreatePackage(sourcePath, result);
            try
            {
                using var sftp = new SftpClient(connection);
                await Task.Run(() => sftp.Connect());

                var remoteDir = windows ? ToSftpPath(installPath) : installPath;
                var remotePackage = $"{remoteDir.TrimEnd('/')}/{PackageFileName}";

                await using (var stream = File.OpenRead(packagePath))
                    await Task.Run(() => sftp.UploadFile(stream, remotePackage, true));
                result.Log.Add($"Uploaded package ({new FileInfo(packagePath).Length / 1024 / 1024} MB)");

                // 4. Ключи API: appsettings.Local.json на агенте (+ HomeLabControl.yaml ниже)
                var agent = ProvisionApiKeys(sftp, remoteDir, req, result);
                agent.Os = req.Os.ToLowerInvariant();
                agent.InstallPath = installPath;
                agent.ServiceName = serviceName;
                agent.SshUser = req.SshUser;
                agent.SshPort = req.SshPort == 22 ? null : req.SshPort;

                // 5. Распаковка
                if (windows)
                {
                    var package = $@"{installPath.TrimEnd('\\')}\{PackageFileName}";
                    RunPowerShell(ssh, $"tar -xzf '{package}' -C '{installPath}'; if ($LASTEXITCODE -ne 0) {{ exit $LASTEXITCODE }}; " +
                                       $"Remove-Item '{package}' -Force", result, throwOnError: true);
                }
                else
                {
                    Run(ssh, $"tar -xzf '{remotePackage}' -C '{installPath}' && rm -f '{remotePackage}' && chmod +x '{installPath}/{executableName}'",
                        result, throwOnError: true);
                }
                result.Log.Add("Package extracted");

                // 6. Служба
                if (windows)
                    InstallWindowsService(ssh, serviceName, $@"{installPath.TrimEnd('\\')}\{executableName}", result);
                else
                    InstallSystemdService(ssh, serviceName, installPath, executableName, result);

                await _configService.UpsertAgentHostAsync(
                    string.IsNullOrWhiteSpace(req.HostName) ? req.Ip : req.HostName.Trim(), req.Ip, agent);
                result.Log.Add("Host saved to HomeLabControl.yaml");
            }
            finally
            {
                File.Delete(packagePath);
            }

            ssh.Disconnect();

            // 7. Проверка: агент поднялся и принимает ключ
            var status = await WaitForAgentAsync(req.Ip, req.AgentPort, result);
            result.Success = status?.State == AgentState.Online;
            result.Message = result.Success
                ? $"Agent v{status!.Version} is running on {req.Ip}:{req.AgentPort}"
                : $"Deployed, but agent is not healthy yet: {status?.StateText ?? "no response"}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent deploy to {Ip} failed", req.Ip);
            result.Success = false;
            result.Message = ex.Message;
            result.Log.Add($"ERROR: {ex.Message}");
        }

        return result;
    }

    /// <summary>Параметры обновления существующего хоста (из секции agent).</summary>
    public AgentDeployRequest CreateUpdateRequest(Host host)
    {
        var agent = host.Agent ?? new AgentSection();
        var os = agent.Os ?? "linux";
        return new AgentDeployRequest
        {
            HostName = host.Name,
            Ip = host.Ip,
            AgentPort = agent.Port,
            Os = os,
            SshUser = agent.SshUser ?? (IsWindows(os) ? "Administrator" : "root"),
            SshPort = agent.SshPort ?? 22,
            ServiceName = agent.ServiceName ?? DefaultServiceName,
            InstallPath = agent.InstallPath ?? GetDefaultInstallPath(os)
        };
    }

    private async Task<AgentStatus?> WaitForAgentAsync(string ip, int port, DeployResult result)
    {
        AgentStatus? status = null;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(2000);
            var host = _configService.FindHost(ip, port);
            if (host == null)
                break;

            status = await _statusService.RefreshAsync(host);
            if (status.State is AgentState.Online or AgentState.KeyRejected)
                break;
        }

        result.Log.Add($"Agent status: {status?.StateText ?? "unknown"}{(status?.Version != null ? $", v{status.Version}" : "")}");
        return status;
    }

    // ─── Uninstall ────────────────────────────────────────────────────────────

    /// <summary>Удалить агент: по SSH (служба + файлы), если uninstallOnHost, и из конфига.</summary>
    public async Task<DeployResult> RemoveAsync(Host host, bool uninstallOnHost, string? sshPassword)
    {
        var result = new DeployResult();

        try
        {
            if (uninstallOnHost)
            {
                var req = CreateUpdateRequest(host);
                var connection = CreateConnectionInfo(req.Ip, req.SshPort, req.SshUser, sshPassword);

                using var ssh = new SshClient(connection);
                await Task.Run(() => ssh.Connect());
                result.Log.Add($"SSH connected to {req.Ip}");

                if (IsWindows(req.Os))
                {
                    RunPowerShell(ssh, $"Stop-Service -Name '{req.ServiceName}' -Force -ErrorAction SilentlyContinue; " +
                                       $"sc.exe delete '{req.ServiceName}' | Out-Null; Start-Sleep -Seconds 2; " +
                                       $"Remove-Item -Recurse -Force '{req.InstallPath}' -ErrorAction SilentlyContinue", result);
                }
                else
                {
                    Run(ssh, $"systemctl disable --now {req.ServiceName} 2>/dev/null; " +
                             $"rm -f /etc/systemd/system/{req.ServiceName}.service; systemctl daemon-reload; " +
                             $"rm -rf '{req.InstallPath}'", result);
                }

                ssh.Disconnect();
                result.Log.Add("Agent service and files removed from host");
            }

            await _configService.RemoveAgentAsync(host.Ip, host.Port);
            _statusService.Forget(host);
            result.Log.Add("Agent removed from HomeLabControl.yaml");
            result.Success = true;
            result.Message = $"Agent removed from {host.Name}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent removal from {Ip} failed", host.Ip);
            result.Success = false;
            result.Message = ex.Message;
            result.Log.Add($"ERROR: {ex.Message}");
        }

        return result;
    }

    // ─── Шаги деплоя ──────────────────────────────────────────────────────────

    private void InstallHlcPublicKey(SshClient ssh, bool windows, DeployResult result)
    {
        var publicKey = File.ReadAllText(KeyPath + ".pub").Trim();

        if (windows)
        {
            // Для администраторов OpenSSH на Windows читает только administrators_authorized_keys,
            // и только если права на файл есть лишь у Administrators (S-1-5-32-544) и SYSTEM (S-1-5-18)
            RunPowerShell(ssh,
                "$f = \"$env:ProgramData\\ssh\\administrators_authorized_keys\"; " +
                $"$k = '{publicKey}'; " +
                "if (!(Test-Path $f) -or !(Select-String -Path $f -SimpleMatch $k -Quiet)) { Add-Content -Path $f -Value $k -Encoding ascii }; " +
                "icacls $f /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null",
                result);
        }
        else
        {
            Run(ssh,
                "mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && " +
                $"(grep -qxF '{publicKey}' ~/.ssh/authorized_keys || echo '{publicKey}' >> ~/.ssh/authorized_keys) && " +
                "chmod 600 ~/.ssh/authorized_keys",
                result);
        }

        result.Log.Add("HLC SSH key installed on host (next updates need no password)");
    }

    private static void InstallSystemdService(SshClient ssh, string serviceName, string installPath, string executableName, DeployResult result)
    {
        var unit = $@"[Unit]
Description=HomeLab Control Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
WorkingDirectory={installPath}
ExecStart={installPath}/{executableName}
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
";
        Run(ssh, $"cat > /etc/systemd/system/{serviceName}.service <<'HLCA_UNIT'\n{unit}HLCA_UNIT", result, throwOnError: true);
        Run(ssh, $"systemctl daemon-reload && systemctl enable {serviceName} && systemctl restart {serviceName}", result, throwOnError: true);
        Run(ssh, $"systemctl is-active {serviceName}", result);
    }

    private static void InstallWindowsService(SshClient ssh, string serviceName, string executablePath, DeployResult result)
    {
        // sc.exe требует пробел после "binPath=", "start=" и т.п.
        RunPowerShell(ssh,
            $"if (Get-Service -Name '{serviceName}' -ErrorAction SilentlyContinue) {{ " +
            $"  sc.exe config '{serviceName}' binPath= '\"{executablePath}\"' start= auto | Out-Null " +
            "} else { " +
            $"  sc.exe create '{serviceName}' binPath= '\"{executablePath}\"' start= auto DisplayName= 'HomeLab Control Agent' | Out-Null " +
            "}; " +
            $"sc.exe failure '{serviceName}' reset= 86400 actions= restart/10000/restart/10000/restart/10000 | Out-Null; " +
            $"Start-Service -Name '{serviceName}'; (Get-Service -Name '{serviceName}').Status",
            result, throwOnError: true);
    }

    /// <summary>
    /// Ключи API — свои для каждого хоста: homelabcontrol (HLC → агент) и homeassistant (rest_command в HA).
    /// Приоритет: уже прописан на агенте → из HomeLabControl.yaml → новый случайный,
    /// поэтому повторный деплой не меняет ключи и не ломает настроенный HA.
    /// </summary>
    private AgentSection ProvisionApiKeys(SftpClient sftp, string remoteDir, AgentDeployRequest req, DeployResult result)
    {
        var remotePath = $"{remoteDir.TrimEnd('/')}/{AgentLocalSettingsFile}";

        JsonObject root = new();
        if (sftp.Exists(remotePath))
        {
            try
            {
                root = JsonNode.Parse(sftp.ReadAllText(remotePath),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    as JsonObject ?? new JsonObject();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{AgentLocalSettingsFile} on target is not valid JSON, fix or delete it: {ex.Message}", ex);
            }
        }

        if (root["Auth"] is not JsonObject auth)
            root["Auth"] = auth = new JsonObject();
        if (auth["ApiKeys"] is not JsonObject apiKeys)
            auth["ApiKeys"] = apiKeys = new JsonObject();

        var existing = _configService.FindHost(req.Ip, req.AgentPort)?.Agent;

        var hlcKey = PickKey(apiKeys, HlcClientName, existing?.ApiKey, result, out _);
        var haKey = PickKey(apiKeys, HaClientName, existing?.HaApiKey, result, out var haIsNew);

        apiKeys[HlcClientName] = hlcKey;
        apiKeys[HaClientName] = haKey;

        // Порт агента, если отличается от стандартного
        if (req.AgentPort != 8117)
            root["ServicePort"] = req.AgentPort;

        sftp.WriteAllText(remotePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        result.Log.Add($"API keys written to {AgentLocalSettingsFile}");

        result.HaApiKey = haKey;
        result.HaKeyIsNew = haIsNew;
        if (haIsNew)
            result.Log.Add("WARNING: new Home Assistant key — add header 'X-Api-Key' to rest_command in HA, otherwise its calls get 401");

        return new AgentSection
        {
            Port = req.AgentPort,
            ApiKey = hlcKey,
            HaApiKey = haKey
        };
    }

    private static string PickKey(JsonObject apiKeys, string client, string? configKey, DeployResult result, out bool isNew)
    {
        isNew = false;

        var remoteKey = apiKeys[client]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(remoteKey))
        {
            result.Log.Add($"Key '{client}': kept existing from host");
            return remoteKey;
        }

        if (!string.IsNullOrWhiteSpace(configKey))
        {
            result.Log.Add($"Key '{client}': taken from HomeLabControl.yaml");
            return configKey;
        }

        isNew = true;
        result.Log.Add($"Key '{client}': generated new");
        return HlcConfigService.GenerateApiKey();
    }

    private static string CreatePackage(string sourcePath, DeployResult result)
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"hlca-{Guid.NewGuid():N}.tar.gz");
        var count = 0;

        using (var file = File.Create(packagePath))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var path in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourcePath, path).Replace('\\', '/');
                if (PreservedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    continue;

                tar.WriteEntry(path, relative);
                count++;
            }
        }

        result.Log.Add($"Package: {count} files");
        return packagePath;
    }

    // ─── SSH helpers ──────────────────────────────────────────────────────────

    private static string ToSftpPath(string windowsPath)
    {
        if (windowsPath.Length >= 2 && char.IsLetter(windowsPath[0]) && windowsPath[1] == ':')
            return $"/{char.ToUpperInvariant(windowsPath[0])}:{windowsPath[2..].Replace('\\', '/')}";
        return windowsPath.Replace('\\', '/');
    }

    /// <summary>Любая команда деплоя дольше этого — зависла (распаковка ~150 МБ укладывается с запасом).</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(3);

    private static string Run(SshClient client, string command, DeployResult result, bool throwOnError = false)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = CommandTimeout;
        var output = cmd.Execute();

        var shown = command.Contains("HLCA_UNIT") ? command[..command.IndexOf('\n')] + " ..." : command;
        result.Log.Add($"> {shown}");
        if (!string.IsNullOrWhiteSpace(output))
            result.Log.Add(output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(cmd.Error))
            result.Log.Add($"STDERR: {cmd.Error.TrimEnd()}");

        if (throwOnError && cmd.ExitStatus != 0)
            throw new InvalidOperationException($"Command failed (exit {cmd.ExitStatus}): {shown}");

        return output;
    }

    /// <summary>PowerShell через -EncodedCommand: без проблем с кавычками cmd.exe → powershell.</summary>
    private static string RunPowerShell(SshClient client, string script, DeployResult result, bool throwOnError = false)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + script));

        using var cmd = client.CreateCommand($"powershell -NoProfile -NonInteractive -EncodedCommand {encoded}");
        cmd.CommandTimeout = CommandTimeout;
        var output = cmd.Execute();

        result.Log.Add($"PS> {script}");
        if (!string.IsNullOrWhiteSpace(output))
            result.Log.Add(output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(cmd.Error) && !cmd.Error.Contains("#< CLIXML"))
            result.Log.Add($"STDERR: {cmd.Error.TrimEnd()}");

        if (throwOnError && cmd.ExitStatus != 0)
            throw new InvalidOperationException($"PowerShell failed (exit {cmd.ExitStatus})");

        return output;
    }
}
