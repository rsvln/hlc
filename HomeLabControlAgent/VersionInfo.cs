using System.Reflection;

namespace HomeLabControlAgent;

/// <summary>Версия из атрибутов сборки, которые задаёт Directory.Build.props (version.txt + дата сборки).</summary>
public static class VersionInfo
{
    private static readonly string Informational =
        typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>X.Y.Z</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>yyyy.MM.dd</summary>
    public static string BuildDate => Informational.Contains('+') ? Informational.Split('+')[1] : "";

    /// <summary>X.Y.Z+yyyy.MM.dd</summary>
    public static string Full => Informational;
}
