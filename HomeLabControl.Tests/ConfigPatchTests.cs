using HomeLabControl.Models;
using HomeLabControl.Services;
using Xunit;

namespace HomeLabControl.Tests;

/// <summary>Правки конфига из UI не должны терять комментарии и должны давать ровно новый конфиг.</summary>
public class ConfigPatchTests
{
    private static readonly string Sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample.yaml"));

    private const string Handwritten = """
        # Мой конфиг
        modules:
          power:
            wolPort: 9          # порт WoL
          monitoring:
            pollIntervalSeconds: 60

        # ─── Машины ───
        hosts:
          - name: nas           # главный сервер
            ip: 192.168.1.10
            agent:
              port: 8117
              apiKey: old-key   # ключ
            power: {}
          # роутер без агента
          - name: router
            ip: 192.168.1.1
            power:
              defaultDelaySeconds: 5
        """;

    /// <summary>Применить изменение, проверить результат и сохранность комментариев; вернуть новый текст.</summary>
    private static string Apply(string text, Action<HlcConfig> mutate, params string[] removedComments)
    {
        var current = HlcConfigService.Deserialize(text);
        var updated = HlcConfigService.Deserialize(HlcConfigService.Serialize(current));
        mutate(updated);

        var patched = HlcConfigService.PatchConfigText(text, current, updated);

        Assert.Equal(HlcConfigService.Serialize(updated), HlcConfigService.Serialize(HlcConfigService.Deserialize(patched)));
        foreach (var comment in Comments(text).Except(removedComments))
            Assert.Contains(comment, patched);
        return patched;
    }

    private static IEnumerable<string> Comments(string text)
        => text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.IndexOf('#') is var i and >= 0 ? l[i..].Trim() : null)
            .Where(c => c is { Length: > 1 })!;

    private static AutomationTask NightlyTask() => new()
    {
        Name = "nightly",
        Schedule = "0 3 * * *",
        Steps =
        {
            new AutomationStep { Wake = new() { "nas" }, TimeoutMinutes = 15 },
            new AutomationStep { Shutdown = new() { "nas" }, Always = true }
        }
    };

    [Fact]
    public void Sample_AddTask_KeepsComments()
        => Apply(Sample, c => c.Modules.Automation.Tasks.Add(NightlyTask()));

    [Fact]
    public void Sample_AddGroup_KeepsComments()
        => Apply(Sample, c => c.Modules.Power.Groups = new() { ["lab"] = new() { c.Hosts[0].Name } });

    [Fact]
    public void Sample_ChangeAgentKey_KeepsComments()
        => Apply(Sample, c => c.Hosts.First(h => h.Agent != null).Agent!.ApiKey = "new-key");

    [Fact]
    public void Sample_AddAndRemoveHost_KeepsComments()
    {
        var added = Apply(Sample, c => c.Hosts.Add(new Host { Name = "new-box", Ip = "192.168.1.99", Power = new PowerSection() }));
        Apply(added, c => c.Hosts.RemoveAll(h => h.Name == "new-box"));
    }

    [Fact]
    public void Handwritten_AgentKey_OnlyThatLineChanges()
    {
        var patched = Apply(Handwritten, c => c.Hosts[0].Agent!.ApiKey = "new-key");

        Assert.Contains("apiKey: new-key", patched);
        Assert.Contains("- name: nas           # главный сервер", patched);
        Assert.Contains("  # роутер без агента", patched);
    }

    [Fact]
    public void Handwritten_AddTaskAndGroup_NewSections()
    {
        var withTask = Apply(Handwritten, c => c.Modules.Automation.Tasks.Add(NightlyTask()));
        var withGroup = Apply(withTask, c => c.Modules.Power.Groups = new() { ["all"] = new() { "nas", "router" } });

        Assert.Contains("wolPort: 9          # порт WoL", withGroup);
        Assert.Contains("automation:", withGroup);
        Assert.Contains("groups:", withGroup);
    }

    [Fact]
    public void Handwritten_DeleteLastTask_RemovesAutomation()
    {
        var withTask = Apply(Handwritten, c => c.Modules.Automation.Tasks.Add(NightlyTask()));
        Apply(withTask, c => c.Modules.Automation.Tasks.Clear());
    }

    [Fact]
    public void Handwritten_RemoveAgentSection_AndNewHostFromDeploy()
    {
        var removed = Apply(Handwritten, c =>
        {
            c.Hosts[0].Agent = null;
            c.Hosts[0].FanControl = null;
        }, "# ключ"); // комментарий внутри удалённой секции agent
        Apply(removed, c => c.Hosts.Add(new Host
        {
            Name = "lab5",
            Ip = "192.168.1.55",
            Agent = new AgentSection { Port = 8117, ApiKey = "k", Os = "linux" },
            Power = new PowerSection(),
            FanControl = new FanControlSection(),
            Smart = new SmartSection()
        }));
    }
}
