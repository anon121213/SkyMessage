using System.Reflection;

namespace SkyMessage.Application.Common;

/// <summary>The version this server was built as (Directory.Build.props: prefix plus the CI build number).</summary>
public static class ServerBuild
{
    /// <summary>"0.2.57": the informational version without source-revision metadata, like the clients report theirs.</summary>
    public static string Version { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(ServerBuild).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
            return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
