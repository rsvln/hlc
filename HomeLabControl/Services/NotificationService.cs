using System.Net;
using System.Text.Json;
using HomeLabControl.Models;

namespace HomeLabControl.Services;

/// <summary>
/// Уведомления (modules.notifications): Telegram и/или MQTT (&lt;baseTopic&gt;/events).
/// Источники — AgentMonitorService (offline/online, SMART) и BackupManagerService (результат бэкапа).
/// </summary>
public class NotificationService : IHostedService
{
    private readonly HlcConfigService _config;
    private readonly AgentMonitorService _monitor;
    private readonly BackupManagerService _backup;
    private readonly MqttService _mqtt;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(HlcConfigService config, AgentMonitorService monitor, BackupManagerService backup, MqttService mqtt,
        IHttpClientFactory httpClientFactory, ILogger<NotificationService> logger)
    {
        _config = config;
        _monitor = monitor;
        _backup = backup;
        _mqtt = mqtt;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitor.EventRaised += OnEvent;
        _backup.BackupFinished += OnBackupFinished;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _monitor.EventRaised -= OnEvent;
        _backup.BackupFinished -= OnBackupFinished;
        return Task.CompletedTask;
    }

    private void OnBackupFinished(BackupJob job)
    {
        if (job.Status == "Skipped")
            return;

        var success = job.Status == "Success";
        var lastLine = job.Log.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
        _monitor.Raise(new HlcEvent
        {
            Type = success ? "backupSuccess" : "backupFailed",
            Host = job.HostName,
            Title = success ? $"Backup of {job.HostName} done" : $"Backup of {job.HostName} FAILED",
            Message = $"{job.Trigger}, {job.StartTime:yyyy-MM-dd HH:mm}: {lastLine}"
        });
    }

    private void OnEvent(HlcEvent e) => _ = SendAsync(e);

    /// <summary>Отправить событие во все включённые каналы (если тип события выбран в events).</summary>
    public async Task SendAsync(HlcEvent e, bool ignoreFilter = false)
    {
        var settings = _config.GetNotifications();
        if (!ignoreFilter && !settings.Events.Contains(e.Type, StringComparer.OrdinalIgnoreCase))
            return;

        if (settings.Telegram.Enabled)
            await SendTelegramAsync(settings.Telegram, e);

        if (settings.Mqtt)
            await _mqtt.PublishEventAsync(e);
    }

    private async Task SendTelegramAsync(TelegramSettings telegram, HlcEvent e)
    {
        if (string.IsNullOrWhiteSpace(telegram.BotToken) || string.IsNullOrWhiteSpace(telegram.ChatId))
        {
            _logger.LogWarning("Telegram notifications enabled, but botToken / chatId are not set");
            return;
        }

        var icon = e.Type switch
        {
            "agentOffline" => "🔴",
            "agentOnline" => "🟢",
            "smart" => "💽",
            "fanProfile" => "🌀",
            "temperature" => "🌡️",
            "diskSpace" => "🗄️",
            "ssdWear" => "💾",
            "backupFailed" => "❌",
            "backupSuccess" => "✅",
            _ => "ℹ️"
        };
        var text = $"{icon} <b>{WebUtility.HtmlEncode(e.Title)}</b>" +
                   (string.IsNullOrEmpty(e.Message) ? "" : $"\n{WebUtility.HtmlEncode(e.Message)}");

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var url = $"{telegram.ApiUrl.TrimEnd('/')}/bot{telegram.BotToken}/sendMessage";
            // StringContent — с Content-Length: не все локальные Bot API и прокси принимают chunked
            var body = JsonSerializer.Serialize(new
            {
                chat_id = telegram.ChatId,
                text,
                parse_mode = "HTML",
                disable_web_page_preview = true
            });
            var response = await client.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Telegram sendMessage failed: {Status} {Body}", (int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            // Токен не пишем в лог: он есть в URL запроса
            _logger.LogWarning("Telegram notification failed: {Error}", ex.Message);
        }
    }
}
