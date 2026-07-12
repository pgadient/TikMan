namespace TikMan.Core.Models;

/// <summary>App language. System = based on the Windows settings at startup.</summary>
public enum AppLanguage
{
    System,
    German,
    English,
    SwissGerman,
    Spanish,
    Italian,
    French,
    Portuguese,
}

/// <summary>How a binary full backup (.backup) is fetched from the device.</summary>
public enum BackupMethod
{
    /// <summary>Try HTTP/WebFig first, otherwise SSH/SCP.</summary>
    Auto,
    /// <summary>HTTP/WebFig only (like the browser).</summary>
    Web,
    /// <summary>SSH/SCP only (port 22, enabled by default, encrypted).</summary>
    Ssh,
}
