using HomeLabControl.Models;
using Renci.SshNet;
using SshConnectionInfo = Renci.SshNet.ConnectionInfo;
using System.Diagnostics;

namespace HomeLabControl.Services
{
    public class BackupManagerService
    {
        private readonly ILogger<BackupManagerService> _logger;
        private readonly HlcConfigService _configService;
        private readonly BackupHistoryService _history;

        // Хосты, бэкап которых выполняется сейчас (повторный запуск того же хоста не нужен)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _running = new();

        // Не больше modules.backup.settings.maxParallelBackups одновременно
        private SemaphoreSlim _parallel = new(3, 3);
        private int _parallelLimit = 3;
        private readonly object _parallelLock = new();

        /// <summary>Бэкап завершён (успешно или нет) — для уведомлений.</summary>
        public event Action<BackupJob>? BackupFinished;

        // Представление из единого HomeLabControl.yaml: modules.backup + хосты с секцией backup (всегда актуальное)
        private BackupManagerConfig _config
        {
            get
            {
                var module = _configService.GetBackupModule();
                return new BackupManagerConfig
                {
                    Settings = module.Settings,
                    Storages = module.Storages,
                    Templates = module.Templates,
                    Hosts = _configService.GetBackupHosts().Select(ToBackupHost).ToList()
                };
            }
        }

        private BackupHost ToBackupHost(Models.Host host)
        {
            var section = host.Backup!;
            var last = _history.GetLast(host.Name);
            var lastSuccess = _history.GetLastSuccess(host.Name);
            return new BackupHost
            {
                Name = host.Name,
                Ip = host.Ip,
                Port = section.SshPort,
                User = section.User,
                SshKey = section.SshKey,
                BackupDirection = section.Direction,
                Template = section.Template,
                Storage = section.Storage,
                Schedule = section.Schedule,
                Enabled = section.Enabled,
                LastBackup = lastSuccess?.StartTime,
                LastStatus = last?.Status ?? "Never"
            };
        }


        public BackupManagerService(ILogger<BackupManagerService> logger, HlcConfigService configService, BackupHistoryService history)
        {
            _logger = logger;
            _configService = configService;
            _history = history;
        }

        public bool IsRunning(string hostName) => _running.ContainsKey(hostName);

        public List<BackupJob> GetHistory(string hostName) => _history.Get(hostName);

        public void ReloadConfig() => _configService.Reload();

        public BackupManagerConfig GetConfig() => _config;

        public List<BackupHost> GetHosts()
        {
            return _config.Hosts;
        }

        public List<BackupTemplate> GetTemplates()
        {
            return _config.Templates;
        }

        public List<BackupStorage> GetStorages()
        {
            return _config.Storages;
        }

        public List<SshKeyInfo> GetSshKeys()
        {
            var keys = new List<SshKeyInfo>();
            var sshKeysPath = _config.Settings.SshKeysPath;

            if (!Directory.Exists(sshKeysPath))
            {
                return keys;
            }

            var privateKeys = Directory.GetFiles(sshKeysPath, "*")
                .Where(f => !f.EndsWith(".pub") && !Path.GetFileName(f).StartsWith("."))
                .ToList();

            foreach (var keyPath in privateKeys)
            {
                var keyName = Path.GetFileName(keyPath);
                var publicKeyPath = keyPath + ".pub";

                var usedBy = new List<string>();

                foreach (var host in _config.Hosts)
                {
                    if (host.SshKey == keyPath)
                    {
                        usedBy.Add($"Host: {host.Name}");
                    }
                }

                foreach (var storage in _config.Storages)
                {
                    if (storage.Type == "ssh" && storage.SshKeyPath == keyPath)
                    {
                        usedBy.Add($"Storage: {storage.Name}");
                    }
                }

                keys.Add(new SshKeyInfo
                {
                    Name = keyName,
                    Path = keyPath,
                    HasPublicKey = File.Exists(publicKeyPath),
                    IsUsed = usedBy.Count > 0,
                    UsedBy = usedBy
                });
            }

            return keys;
        }

        public async Task<string> GenerateSshKeyAsync(string keyName)
        {
            try
            {
                // Только имя файла: "../../etc/..." позволяло писать ключ куда угодно
                if (string.IsNullOrWhiteSpace(keyName) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(keyName, @"^[A-Za-z0-9._-]+$") ||
                    keyName.StartsWith('.'))
                {
                    throw new ArgumentException("Key name may contain only letters, digits, '.', '_' and '-'");
                }

                var keyPath = Path.Combine(_config.Settings.SshKeysPath, keyName);

                if (File.Exists(keyPath))
                {
                    throw new Exception($"Key {keyName} already exists");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "ssh-keygen",
                    Arguments = $"-t rsa -b 4096 -f {keyPath} -N \"\" -C \"{keyName}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    throw new Exception("Failed to start ssh-keygen process");
                }

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    throw new Exception($"ssh-keygen failed: {error}");
                }

                _logger.LogInformation($"Generated SSH key: {keyPath}");
                return keyPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to generate SSH key: {keyName}");
                throw;
            }
        }

        public bool DeleteSshKey(string keyPath)
        {
            try
            {
                var keyInfo = GetSshKeys().FirstOrDefault(k => k.Path == keyPath);
                if (keyInfo == null)
                {
                    throw new Exception("Key not found");
                }

                if (keyInfo.IsUsed)
                {
                    throw new Exception($"Key is in use by: {string.Join(", ", keyInfo.UsedBy)}");
                }

                if (File.Exists(keyPath))
                {
                    File.Delete(keyPath);
                }

                var publicKeyPath = keyPath + ".pub";
                if (File.Exists(publicKeyPath))
                {
                    File.Delete(publicKeyPath);
                }

                _logger.LogInformation($"Deleted SSH key: {keyPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete SSH key: {keyPath}");
                throw;
            }
        }

        public async Task<BackupJob> RunBackupAsync(string hostName, string trigger = "manual")
        {
            var job = new BackupJob
            {
                HostName = hostName,
                StartTime = DateTime.Now,
                Status = "Running",
                Trigger = trigger
            };

            if (!_running.TryAdd(hostName, 0))
            {
                job.Status = "Skipped";
                job.EndTime = DateTime.Now;
                job.Log = $"[{DateTime.Now:HH:mm:ss}] Backup of {hostName} is already running\n";
                return job;
            }

            var parallel = GetParallelLimiter();
            var waited = !await parallel.WaitAsync(0);
            if (waited)
            {
                job.Log += $"[{DateTime.Now:HH:mm:ss}] Waiting: {_parallelLimit} backup(s) already running\n";
                await parallel.WaitAsync();
                job.StartTime = DateTime.Now;
            }

            try
            {
                var host = _config.Hosts.FirstOrDefault(h => h.Name == hostName);
                if (host == null)
                {
                    throw new Exception($"Host {hostName} not found");
                }

                var template = _config.Templates.FirstOrDefault(t => t.Name == host.Template);
                if (template == null)
                {
                    throw new Exception($"Template {host.Template} not found");
                }

                var storage = _config.Storages.FirstOrDefault(s => s.Name == host.Storage);
                if (storage == null)
                {
                    throw new Exception($"Storage {host.Storage} not found");
                }

                _logger.LogInformation($"Starting {host.BackupDirection.ToUpper()} backup for {hostName}");
                job.Log += $"[{DateTime.Now:HH:mm:ss}] Starting {host.BackupDirection.ToUpper()} backup for {hostName}\n";
                job.Log += $"[{DateTime.Now:HH:mm:ss}] Template: {template.Name}, Storage: {storage.Name} ({storage.Type})\n";

                if (host.BackupDirection.ToLower() == "push")
                {
                    await RunPushBackupAsync(host, template, storage, job);
                }
                else if (host.BackupDirection.ToLower() == "pull")
                {
                    await RunPullBackupAsync(host, template, storage, job);
                }
                else
                {
                    throw new Exception($"Unknown backup direction: {host.BackupDirection}");
                }

                job.Status = "Success";
                job.Log += $"[{DateTime.Now:HH:mm:ss}] Backup completed successfully\n";

                // Ротация: удаляем каталоги старше retentionDays (шаблона или общих настроек)
                var retentionDays = template.RetentionDays ?? _config.Settings.RetentionDays;
                if (retentionDays > 0)
                {
                    try
                    {
                        await ApplyRetentionAsync(host, storage, retentionDays, job);
                    }
                    catch (Exception ex)
                    {
                        // Бэкап сделан — ошибка ротации не делает его неудачным
                        job.Log += $"[{DateTime.Now:HH:mm:ss}] WARNING: retention failed: {ex.Message}\n";
                        _logger.LogWarning(ex, "Backup retention for {Host} failed", hostName);
                    }
                }

                job.EndTime = DateTime.Now;

                _logger.LogInformation($"Backup for {hostName} completed successfully");
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                job.EndTime = DateTime.Now;
                job.Log += $"[{DateTime.Now:HH:mm:ss}] FAILED: {ex.Message}\n";

                _logger.LogError(ex, $"Backup for {hostName} failed");
            }
            finally
            {
                parallel.Release();
                _running.TryRemove(hostName, out _);
            }

            _history.Add(job);
            try
            {
                BackupFinished?.Invoke(job);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BackupFinished handler failed");
            }

            return job;
        }

        private SemaphoreSlim GetParallelLimiter()
        {
            var limit = Math.Max(1, _config.Settings.MaxParallelBackups);
            lock (_parallelLock)
            {
                // Лимит поменяли в конфиге — новые запуски идут через новый семафор, текущие доработают на старом
                if (limit != _parallelLimit)
                {
                    _parallel = new SemaphoreSlim(limit, limit);
                    _parallelLimit = limit;
                }
                return _parallel;
            }
        }

        // ─── Ротация ──────────────────────────────────────────────────────────

        private static readonly System.Text.RegularExpressions.Regex BackupDirRx = new(@"^\d{8}_\d{6}$");

        /// <summary>
        /// Удаляет каталоги бэкапа старше retentionDays в &lt;storage&gt;/&lt;host&gt;/.
        /// Трогает только каталоги с именем вида yyyyMMdd_HHmmss (формат {{DATE}}) и всегда оставляет самый свежий.
        /// Шаблоны должны класть бэкапы в {{STORAGE_REMOTE_PATH}}/{{HOST_NAME}}/{{DATE}}.
        /// </summary>
        private async Task ApplyRetentionAsync(BackupHost host, BackupStorage storage, int retentionDays, BackupJob job)
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);

            if (storage.Type == "ssh")
            {
                if (string.IsNullOrEmpty(storage.SshKeyPath) || !File.Exists(storage.SshKeyPath))
                    throw new Exception($"Storage SSH key not found: {storage.SshKeyPath}");

                var baseDir = $"{storage.RemotePath.TrimEnd('/')}/{host.Name}";
                var connectionInfo = new SshConnectionInfo(storage.SshHost, storage.SshPort, storage.SshUser,
                    new PrivateKeyAuthenticationMethod(storage.SshUser, new PrivateKeyFile(storage.SshKeyPath)));

                using var client = new SshClient(connectionInfo);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.Settings.TimeoutSeconds);
                await Task.Run(() => client.Connect());

                var listing = await Task.Run(() => client.RunCommand($"ls -1 {ShellQuote(baseDir)} 2>/dev/null").Result);
                var expired = SelectExpired(listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), cutoff);

                foreach (var name in expired)
                {
                    await Task.Run(() => client.RunCommand($"rm -rf {ShellQuote(baseDir + "/" + name)}"));
                    job.Log += $"[{DateTime.Now:HH:mm:ss}] Retention: removed {baseDir}/{name}\n";
                }

                client.Disconnect();
            }
            else
            {
                var baseDir = Path.Combine(storage.LocalPath, host.Name);
                if (!Directory.Exists(baseDir))
                    return;

                var expired = SelectExpired(Directory.GetDirectories(baseDir).Select(Path.GetFileName).OfType<string>(), cutoff);
                foreach (var name in expired)
                {
                    Directory.Delete(Path.Combine(baseDir, name), recursive: true);
                    job.Log += $"[{DateTime.Now:HH:mm:ss}] Retention: removed {Path.Combine(baseDir, name)}\n";
                }
            }
        }

        /// <summary>Каталоги yyyyMMdd_HHmmss старше cutoff, кроме самого свежего.</summary>
        internal static List<string> SelectExpired(IEnumerable<string> names, DateTime cutoff)
        {
            var dated = names
                .Where(n => BackupDirRx.IsMatch(n))
                .Select(n => (Name: n, Date: DateTime.ParseExact(n, "yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)))
                .OrderByDescending(d => d.Date)
                .ToList();

            return dated.Skip(1).Where(d => d.Date < cutoff).Select(d => d.Name).ToList();
        }

        private async Task RunPushBackupAsync(BackupHost host, BackupTemplate template, BackupStorage storage, BackupJob job)
        {
            // PUSH: BackupManager → Router, Router выполняет команды и пушит на Storage
            if (string.IsNullOrEmpty(host.SshKey) || !File.Exists(host.SshKey))
            {
                throw new Exception($"Host SSH key not found: {host.SshKey}");
            }

            var keyFile = new PrivateKeyFile(host.SshKey);
            var connectionInfo = new SshConnectionInfo(host.Ip, host.Port, host.User,
                new PrivateKeyAuthenticationMethod(host.User, keyFile));

            using var client = new SshClient(connectionInfo);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.Settings.TimeoutSeconds);

            await Task.Run(() => client.Connect());
            job.Log += $"[{DateTime.Now:HH:mm:ss}] Connected to {host.Ip}\n";

            // Одна дата на всю задачу: раньше {{DATE}} вычислялся на каждую команду,
            // и mkdir/scp/rm могли получить разные каталоги при смене секунды
            var date = job.StartTime.ToString("yyyyMMdd_HHmmss");

            foreach (var cmdTemplate in template.PushCommands)
            {
                var cmd = ReplaceVariablesForPush(cmdTemplate, host, storage, date);
                job.Log += $"[{DateTime.Now:HH:mm:ss}] Executing: {cmd}\n";

                var result = await Task.Run(() => client.RunCommand(cmd));

                if (result.ExitStatus != 0)
                {
                    job.Log += $"[{DateTime.Now:HH:mm:ss}] ERROR: {result.Error}\n";
                    throw new Exception($"Command failed: {result.Error}");
                }

                if (!string.IsNullOrWhiteSpace(result.Result))
                {
                    job.Log += $"[{DateTime.Now:HH:mm:ss}] Output: {result.Result}\n";
                }
            }

            client.Disconnect();
        }

        private async Task RunPullBackupAsync(BackupHost host, BackupTemplate template, BackupStorage storage, BackupJob job)
        {
            // PULL: BackupManager → Storage, Storage вытягивает с Router
            if (storage.Type != "ssh")
            {
                throw new Exception("PULL mode requires SSH storage");
            }

            if (string.IsNullOrEmpty(storage.SshKeyPath) || !File.Exists(storage.SshKeyPath))
            {
                throw new Exception($"Storage SSH key not found: {storage.SshKeyPath}");
            }

            var keyFile = new PrivateKeyFile(storage.SshKeyPath);
            var connectionInfo = new SshConnectionInfo(storage.SshHost, storage.SshPort, storage.SshUser,
                new PrivateKeyAuthenticationMethod(storage.SshUser, keyFile));

            using var client = new SshClient(connectionInfo);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.Settings.TimeoutSeconds);

            await Task.Run(() => client.Connect());
            job.Log += $"[{DateTime.Now:HH:mm:ss}] Connected to storage {storage.SshHost}\n";

            var date = job.StartTime.ToString("yyyyMMdd_HHmmss");

            foreach (var cmdTemplate in template.PullCommands)
            {
                var cmd = ReplaceVariablesForPull(cmdTemplate, host, storage, date);
                job.Log += $"[{DateTime.Now:HH:mm:ss}] Executing on storage: {cmd}\n";

                var result = await Task.Run(() => client.RunCommand(cmd));

                if (result.ExitStatus != 0)
                {
                    job.Log += $"[{DateTime.Now:HH:mm:ss}] ERROR: {result.Error}\n";
                    throw new Exception($"Command failed: {result.Error}");
                }

                if (!string.IsNullOrWhiteSpace(result.Result))
                {
                    job.Log += $"[{DateTime.Now:HH:mm:ss}] Output: {result.Result}\n";
                }
            }

            client.Disconnect();
        }

        private string ReplaceVariablesForPush(string template, BackupHost host, BackupStorage storage, string date)
        {

            var result = template
                .Replace("{{DATE}}", date)
                .Replace("{{HOST_NAME}}", host.Name)
                .Replace("{{STORAGE_HOST}}", storage.SshHost)
                .Replace("{{STORAGE_PORT}}", storage.SshPort.ToString())
                .Replace("{{STORAGE_USER}}", storage.SshUser)
                .Replace("{{STORAGE_REMOTE_PATH}}", storage.RemotePath)
                .Replace("{{STORAGE_KEY}}", Path.GetFileName(storage.SshKeyPath)); // Имя ключа на роутере

            return result;
        }

        private string ReplaceVariablesForPull(string template, BackupHost host, BackupStorage storage, string date)
        {

            var result = template
                .Replace("{{DATE}}", date)
                .Replace("{{HOST_NAME}}", host.Name)
                .Replace("{{HOST_IP}}", host.Ip)
                .Replace("{{HOST_PORT}}", host.Port.ToString())
                .Replace("{{HOST_USER}}", host.User)
                .Replace("{{HOST_KEY}}", Path.GetFileName(host.SshKey)) // Имя ключа на storage
                .Replace("{{STORAGE_REMOTE_PATH}}", storage.RemotePath);

            return result;
        }

        public async Task<bool> DeployAllKeysAsync(string hostName, string hostPassword, string storagePassword)
        {
            try
            {
                var host = _config.Hosts.FirstOrDefault(h => h.Name == hostName);
                if (host == null)
                {
                    throw new Exception($"Host {hostName} not found");
                }

                var storage = _config.Storages.FirstOrDefault(s => s.Name == host.Storage);
                if (storage == null)
                {
                    throw new Exception($"Storage {host.Storage} not found");
                }

                if (host.BackupDirection.ToLower() == "push")
                {
                    return await DeployPushKeysAsync(host, storage, hostPassword, storagePassword);
                }
                else if (host.BackupDirection.ToLower() == "pull")
                {
                    return await DeployPullKeysAsync(host, storage, hostPassword, storagePassword);
                }
                else
                {
                    throw new Exception($"Unknown backup direction: {host.BackupDirection}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to deploy keys for {hostName}");
                throw;
            }
        }

        private async Task<bool> DeployPushKeysAsync(BackupHost host, BackupStorage storage, string hostPassword, string storagePassword)
        {
            // PUSH: Deploy host_key.pub → Router, storage_key.pub → Storage, storage_key (private) → Router

            // 1. Deploy host key public to Router
            if (string.IsNullOrEmpty(host.SshKey) || !File.Exists(host.SshKey))
            {
                throw new Exception($"Host SSH key not found: {host.SshKey}");
            }

            var hostPublicKey = File.ReadAllText(host.SshKey + ".pub").Trim();

            var hostConnectionInfo = new SshConnectionInfo(host.Ip, host.Port, host.User,
                new PasswordAuthenticationMethod(host.User, hostPassword));

            using (var client = new SshClient(hostConnectionInfo))
            {
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.Settings.TimeoutSeconds);
                await Task.Run(() => client.Connect());

                var cmd = $"mkdir -p ~/.ssh && echo {ShellQuote(hostPublicKey)} >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys";
                await Task.Run(() => client.RunCommand(cmd));

                client.Disconnect();
            }

            _logger.LogInformation($"Deployed host public key to {host.Name}");

            //await Task.Delay(2000);

            // 2. Deploy storage key to Storage and Router (for SSH storage)
            if (storage.Type == "ssh")
            {
                if (string.IsNullOrEmpty(storage.SshKeyPath) || !File.Exists(storage.SshKeyPath))
                {
                    throw new Exception($"Storage SSH key not found: {storage.SshKeyPath}");
                }

                var storagePublicKey = File.ReadAllText(storage.SshKeyPath + ".pub").Trim();

                // Deploy public key to Storage
                var storageConnectionInfo = new SshConnectionInfo(storage.SshHost, storage.SshPort, storage.SshUser,
                    new PasswordAuthenticationMethod(storage.SshUser, storagePassword));

                using (var client = new SshClient(storageConnectionInfo))
                {
                    client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.Settings.TimeoutSeconds);
                    await Task.Run(() => client.Connect());

                    var cmd = $"mkdir -p ~/.ssh && echo {ShellQuote(storagePublicKey)} >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys";
                    await Task.Run(() => client.RunCommand(cmd));

                    client.Disconnect();
                }

                _logger.LogInformation($"Deployed storage public key to {storage.Name}");

                // Deploy private key to Router
                var hostKeyFile = new PrivateKeyFile(host.SshKey);
                var hostAuthConnectionInfo = new SshConnectionInfo(host.Ip, host.Port, host.User,
                    new PrivateKeyAuthenticationMethod(host.User, hostKeyFile));

                using (var scp = CreateScpClient(hostAuthConnectionInfo))
                {
                    await Task.Run(() => scp.Connect());

                    var privateKeyContent = File.ReadAllBytes(storage.SshKeyPath);
                    var remoteKeyPath = $"/root/.ssh/{Path.GetFileName(storage.SshKeyPath)}";
                    await Task.Run(() => scp.Upload(new MemoryStream(privateKeyContent), remoteKeyPath));

                    scp.Disconnect();
                }

                using (var client = new SshClient(hostAuthConnectionInfo))
                {
                    await Task.Run(() => client.Connect());

                    var cmd = $"chmod 600 {ShellQuote("/root/.ssh/" + Path.GetFileName(storage.SshKeyPath))}";
                    await Task.Run(() => client.RunCommand(cmd));

                    client.Disconnect();
                }

                _logger.LogInformation($"Deployed storage private key to {host.Name}");
            }

            return true;
        }

        private async Task<bool> DeployPullKeysAsync(BackupHost host, BackupStorage storage, string hostPassword, string storagePassword)
        {
            // PULL: Deploy storage_key.pub → Storage, host_key.pub → Router, host_key (private) → Storage

            if (storage.Type != "ssh")
            {
                throw new Exception("PULL mode requires SSH storage");
            }

            // 1. Deploy storage key public to Storage (BackupManager → Storage)
            if (string.IsNullOrEmpty(storage.SshKeyPath) || !File.Exists(storage.SshKeyPath))
            {
                throw new Exception($"Storage SSH key not found: {storage.SshKeyPath}");
            }

            var storagePublicKey = File.ReadAllText(storage.SshKeyPath + ".pub").Trim();

            var storageConnectionInfo = new SshConnectionInfo(storage.SshHost, storage.SshPort, storage.SshUser,
                new PasswordAuthenticationMethod(storage.SshUser, storagePassword));

            using (var client = new SshClient(storageConnectionInfo))
            {
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.Settings.TimeoutSeconds);
                await Task.Run(() => client.Connect());

                var cmd = $"mkdir -p ~/.ssh && echo {ShellQuote(storagePublicKey)} >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys";
                await Task.Run(() => client.RunCommand(cmd));

                client.Disconnect();
            }

            _logger.LogInformation($"Deployed storage public key to {storage.Name}");

            // 2. Deploy host key public to Router (Storage → Router)
            if (string.IsNullOrEmpty(host.SshKey) || !File.Exists(host.SshKey))
            {
                throw new Exception($"Host SSH key not found: {host.SshKey}");
            }

            var hostPublicKey = File.ReadAllText(host.SshKey + ".pub").Trim();

            var hostConnectionInfo = new SshConnectionInfo(host.Ip, host.Port, host.User,
                new PasswordAuthenticationMethod(host.User, hostPassword));

            using (var client = new SshClient(hostConnectionInfo))
            {
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.Settings.TimeoutSeconds);
                await Task.Run(() => client.Connect());

                var cmd = $"mkdir -p ~/.ssh && echo {ShellQuote(hostPublicKey)} >> ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys";
                await Task.Run(() => client.RunCommand(cmd));

                client.Disconnect();
            }

            _logger.LogInformation($"Deployed host public key to {host.Name}");

            // 3. Deploy host private key to Storage
            var storageKeyFile = new PrivateKeyFile(storage.SshKeyPath);
            var storageAuthConnectionInfo = new SshConnectionInfo(storage.SshHost, storage.SshPort, storage.SshUser,
                new PrivateKeyAuthenticationMethod(storage.SshUser, storageKeyFile));

            using (var scp = CreateScpClient(storageAuthConnectionInfo))
            {
                await Task.Run(() => scp.Connect());

                var privateKeyContent = File.ReadAllBytes(host.SshKey);
                var remoteKeyPath = $"{storage.RemotePath}/.ssh/{Path.GetFileName(host.SshKey)}";

                // Создаем .ssh директорию на storage
                using (var client = new SshClient(storageAuthConnectionInfo))
                {
                    await Task.Run(() => client.Connect());
                    await Task.Run(() => client.RunCommand($"mkdir -p {ShellQuote(storage.RemotePath + "/.ssh")}"));
                    client.Disconnect();
                }

                await Task.Run(() => scp.Upload(new MemoryStream(privateKeyContent), remoteKeyPath));

                scp.Disconnect();
            }

            using (var client = new SshClient(storageAuthConnectionInfo))
            {
                await Task.Run(() => client.Connect());

                var cmd = $"chmod 600 {ShellQuote(storage.RemotePath + "/.ssh/" + Path.GetFileName(host.SshKey))}";
                await Task.Run(() => client.RunCommand(cmd));

                client.Disconnect();
            }

            _logger.LogInformation($"Deployed host private key to {storage.Name}");

            return true;
        }
    
        /// <summary>Строка в одинарных кавычках для POSIX-оболочки (ключи и пути вставляются в команды).</summary>
        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        /// <summary>
        /// SCP, а не SFTP: на OpenWrt (Dropbear) SFTP-сервера обычно нет. Путь на удалённой стороне экранируется
        /// (ShellQuote) — старый конструктор без трансформации позволял инъекцию команд через путь.
        /// </summary>
        private static ScpClient CreateScpClient(SshConnectionInfo connectionInfo)
            => new(connectionInfo, RemotePathTransformation.ShellQuote);
    }

}