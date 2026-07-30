using System;
using System.Reflection;

namespace TikMan.Core;

/// <summary>The one place that decides what "the version" looks like. Assemblies always carry four
/// components (<c>System.Version</c> has no three-part form), so <c>ToString()</c> yields "2.2.2.0" while
/// the project, the GitHub releases and the release asset names all say "2.2.2".
/// <para>⚠️ Never call <c>Version.ToString()</c> on an assembly version for anything the user or the
/// updater sees. The WPF client and the release tags use Major.Minor.Build; a second scheme appearing in
/// the Avalonia client would make the migration between the two look like a downgrade.</para></summary>
public static class AppVersion
{
    /// <summary>The running build's version, trimmed to the three components the project actually uses.</summary>
    public static Version Current(Assembly? asm = null)
    {
        var v = (asm ?? Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly).GetName().Version;
        return v is null ? new Version(0, 0, 0) : Three(v);
    }

    /// <summary>Drops the revision component: 2.2.2.0 → 2.2.2.</summary>
    public static Version Three(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Display form – "2.2.2", the same string the release tag and the asset names use.</summary>
    public static string Text(Version v) => Three(v).ToString();

    /// <summary>Display form for the running build.</summary>
    public static string Text(Assembly? asm = null) => Text(Current(asm));
}
