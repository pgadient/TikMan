using System.Text.Json;
using System.Text.Json.Serialization;
using TikMan.Core.Models;


namespace TikMan.Core.Storage;

/// <summary>Persisted app data (device list + settings).</summary>
/// <summary>How much of a scheduled check is worth an e-mail.</summary>
public enum NotifyLevel
{
    /// <summary>Only when something went wrong – a device that couldn't be reached or checked.</summary>
    ErrorsOnly,
    /// <summary>Every run, including "nothing to do" – proof the schedule is alive.</summary>
    Info,
}

/// <summary>Which SSH client TikMan hands a session to.</summary>
public enum SshClientKind
{
    /// <summary>TikMan's own terminal – a real SSH connection made by the app, identical on every platform
    /// and needing nothing installed.</summary>
    BuiltIn,
    /// <summary>The operating system's own <c>ssh</c>, opened in a terminal window.</summary>
    System,
    /// <summary>A client the user points at (PuTTY, KiTTY, …) – see <see cref="AppData.ExternalSshClientPath"/>.</summary>
    ThirdParty,
}

public class AppData
{
    public int Version { get; set; } = 1;

    // ---- Scheduled update check + e-mail notification -------------------------------------------
    // Deliberately a *check*, not an install: it only reads, so a run at 03:00 can't leave a device
    // half-updated or a router rebooting with nobody watching. Installing on a schedule is a separate
    // decision with separate safeguards.

    /// <summary>Run the update check on a schedule (off by default).</summary>
    public bool AutoCheckEnabled { get; set; }

    /// <summary>Time of day for the check, "HH:mm". ⚠️ TikMan is a desktop app: the schedule only fires
    /// while it is running. Missing the slot isn't fatal – <see cref="LastAutoCheck"/> makes the next
    /// start catch up on a slot that has passed today.</summary>
    public string AutoCheckTime { get; set; } = "03:00";

    /// <summary>When the scheduled check last ran, so a slot is caught up rather than run twice.</summary>
    public DateTime? LastAutoCheck { get; set; }

    /// <summary>Whether a clean run ("nothing to do") is worth an e-mail too, or only failures.</summary>
    public NotifyLevel NotifyLevel { get; set; } = NotifyLevel.Info;

    public string SmtpHost { get; set; } = "";
    /// <summary>587 = STARTTLS, the usual submission port. 465 (implicit TLS) also works.</summary>
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseTls { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    /// <summary>SMTP password, DPAPI-encrypted – never in clear text, like every other password here.</summary>
    public string SmtpEncryptedPassword { get; set; } = "";
    public string MailFrom { get; set; } = "";
    /// <summary>Recipients, comma-separated.</summary>
    public string MailTo { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 30;
    public bool AutoRefreshEnabled { get; set; }
    /// <summary>The continuous alive check (one TCP connect per device to a known-open port, feeding the
    /// traffic-light column). On by default; off means no background reachability probing at all.</summary>
    public bool AliveCheckEnabled { get; set; } = true;
    public int AliveCheckSeconds { get; set; } = 5;
    public bool LogAutoRefresh { get; set; } = true;
    /// <summary>How often the log view refetches while auto-refresh is on, in seconds. The view offers a
    /// small fixed set (1/5/15/30/60); an out-of-set value from an older file is snapped to the nearest.</summary>
    public int LogRefreshSeconds { get; set; } = 5;
    /// <summary>How many log entries to fetch by default (the view's "# entries" dropdown: 100/500/all).</summary>
    public int LogRowCap { get; set; } = 100;
    /// <summary>How often the background monitor re-reads CPU/RAM/uptime, in seconds. Drives the monitoring
    /// tab's interval dropdown (5/10/15/30/60/120). Kept separate from the meta refresh below, which stays on
    /// a slower floor so a fast monitoring cadence never turns into a full re-fingerprint of every device.</summary>
    public int MonitorIntervalSeconds { get; set; } = 30;
    public AppLanguage Language { get; set; } = AppLanguage.System;

    /// <summary>Light/dark appearance of the cross-platform GUI; System follows the OS.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;
    public BackupMethod BackupMethod { get; set; } = BackupMethod.Auto;
    public int SshPort { get; set; } = 22;
    /// <summary>Default username offered for devices added from a scan.</summary>
    public string DefaultUsername { get; set; } = "admin";
    /// <summary>Default password (DPAPI-encrypted) for devices added from a scan.</summary>
    public string DefaultEncryptedPassword { get; set; } = "";
    /// <summary>How many devices a targeted re-read talks to at once (context-menu rescan, and the pass
    /// that follows saving credentials).
    /// <para>Most of a re-read is waiting for a connection to be established – an older switch can spend
    /// several seconds in the SSH handshake before any data moves – so these overlap well. Raise it for a
    /// large site, lower it if a device or a link objects to the concurrency. Clamped to 1–32.</para></summary>
    public int ParallelDeviceReads { get; set; } = 8;

    /// <summary>Update channel used for devices that don't override it themselves.</summary>
    public string DefaultUpdateChannel { get; set; } = "stable";
    /// <summary>Whether <see cref="DefaultUpdateChannel"/> is applied to <b>every</b> device rather than
    /// each device keeping its own.
    /// <para>Off by default, and deliberately so: switching a whole fleet's release channel in one gesture
    /// is a real change to every device, and it should be something the user asks for, not the state they
    /// find the assistant in. While it is off, a device's channel is left exactly as it is unless the row
    /// says otherwise.</para></summary>
    public bool OneUpdateChannelForAll { get; set; }
    /// <summary>Default for new devices: accept self-signed/invalid TLS certificates (common on a
    /// LAN with MikroTik's default self-signed cert). On by default.</summary>
    public bool DefaultIgnoreCertErrors { get; set; } = true;
    /// <summary>Whether the list shows a device's IPv4 and IPv6 address combined in one row.
    /// Off by default (matched addresses are shown on separate rows unless the user turns it on).</summary>
    public bool CombineAddresses { get; set; }
    /// <summary>Allow logging in over plain HTTP when HTTPS fails (credentials then travel in clear
    /// text). Off by default; when on, TLS-failed devices are retried over HTTP on the next refresh.</summary>
    public bool AllowHttpFallback { get; set; }
    /// <summary>Force the problem report to use the default-mail (mailto) path with the log inline,
    /// instead of preferring Outlook Classic with a file attachment. Off by default.</summary>
    public bool ForceMailFallback { get; set; }
    /// <summary>Coffee button size: "normal", "small", or "off".</summary>
    public string CoffeeButton { get; set; } = "normal";
    /// <summary>Expand device rows by default, so all IPv6 addresses and SMB shares are visible
    /// without clicking the + expander.</summary>
    public bool ExpandRowsByDefault { get; set; }
    /// <summary>Which address tab the list last showed: false = IPv4 (default), true = IPv6.</summary>
    public bool ShowIpv6View { get; set; }
    /// <summary>Show the coloured "Report a problem" / "Request a feature" buttons (default on).</summary>
    public bool ShowContactButtons { get; set; } = true;
    /// <summary>Show the ⓘ list-tips icon above the device list (default on).</summary>
    public bool ShowListInfo { get; set; } = true;
    /// <summary>Per-host ping timeout in milliseconds during a subnet scan (default 600).</summary>
    public int PingTimeoutMs { get; set; } = 600;
    /// <summary>Extra ping attempts per host after the first, to ride out a lost packet (default 0 –
    /// continuous scan re-finds a briefly-missed host on the next pass anyway).</summary>
    public int PingRetries { get; set; }
    /// <summary>Show the "a standalone client is safer" notice before the built-in VNC viewer (default on).</summary>
    public bool ShowVncNotice { get; set; } = true;
    /// <summary>Simple / corporate mode: only the plain IPv4 address scan (ping + TCP ports). No MNDP,
    /// ZON, IPv6 discovery, mDNS, UPnP/SSDP or per-device SNMP/WMI/web probing – so nothing but ordinary
    /// connections goes on the wire, which a locked-down corporate network won't flag. Off by default.</summary>
    public bool SimpleScanMode { get; set; }
    /// <summary>Skip the automatic discovery scan on startup (off by default – normally we scan at once).</summary>
    public bool NoInitialScan { get; set; }
    /// <summary>Check GitHub for a newer release on startup and offer to update (on by default).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Read-only SNMP community for the probes and the FDB reads on the physical topology.
    /// "public" is what most gear ships with; a site that changed it enters its own here.</summary>
    public string SnmpCommunity { get; set; } = "public";
    /// <summary>Show the discovery phases as one combined bar instead of one bar per phase. The phases
    /// run in parallel and each on its own clock, so a single "how far along is the scan" bar reads far
    /// more easily than seven – hence on by default.</summary>
    public bool SingleProgressBar { get; set; } = true;
    /// <summary>Open ssh sessions with an external client (see <see cref="ExternalSshClientPath"/>)
    /// instead of the built-in OpenSSH terminal.</summary>
    public bool UseExternalSshClient { get; set; }
    /// <summary>Open SSH sessions in TikMan's <b>own</b> terminal instead of handing them to a client on
    /// this machine.
    /// <para>The built-in one is a real SSH connection made by TikMan (same code the web terminal uses), so
    /// it works the same on Windows, Linux and macOS and needs nothing installed. It asks for a login when
    /// the device has none stored. Off by default, so the existing behaviour – the system <c>ssh</c>, or the
    /// external client below – is unchanged for anyone who has not asked for this.</para>
    /// <para>⚠️ Takes precedence over <see cref="UseExternalSshClient"/>: two switches that both claim to
    /// choose the SSH client need a stated winner, or the answer depends on which one is read first.</para></summary>
    public bool PreferBuiltInSsh { get; set; }
    /// <summary>Path to the external SSH client executable (e.g. PuTTY).</summary>
    public string ExternalSshClientPath { get; set; } = "";

    /// <summary>Which SSH client opens a session – <b>one</b> choice instead of two booleans.
    ///
    /// <para>⚠️ Deliberately NOT persisted: it reads and writes the two flags above, which stay the stored
    /// form. That keeps every existing settings file working exactly as it did – no migration, nothing to
    /// get wrong on the way in – while the ambiguity disappears at the only place it mattered. Two
    /// independent switches could both say yes, which is why <see cref="PreferBuiltInSsh"/> had to document
    /// a precedence rule; assigning through here makes them mutually exclusive by construction, and the
    /// getter states that same precedence for any older file that has both set.</para></summary>
    [JsonIgnore]
    public SshClientKind SshClient
    {
        get => PreferBuiltInSsh ? SshClientKind.BuiltIn
             : UseExternalSshClient ? SshClientKind.ThirdParty
             : SshClientKind.System;
        set
        {
            PreferBuiltInSsh = value == SshClientKind.BuiltIn;
            UseExternalSshClient = value == SshClientKind.ThirdParty;
        }
    }
    /// <summary>Path to VLC (or another player) for the RTSP camera preview. When empty, the rtsp://
    /// badge falls back to whatever player registered the scheme system-wide.</summary>
    public string VlcPath { get; set; } = "";
    /// <summary>Path to WinSCP.exe, for the "Open in WinSCP" context-menu action.</summary>
    public string WinScpPath { get; set; } = "";
    /// <summary>Extra TCP ports to probe on top of the built-in service list, comma-separated
    /// ("8006, 9000, 32400"). Empty by default.
    /// <para>The built-in list covers the ports that <i>identify</i> a device; this is for the ones that
    /// matter in a particular network – a Proxmox host, a Plex server, an in-house service – which no
    /// general list could know about.</para></summary>
    public string CustomPorts { get; set; } = "";

    /// <summary>Also port-scan hosts that do not answer a ping. Off by default.
    /// <para>ICMP is the scan's liveness gate, and a host that blocks it – which Windows does out of the
    /// box – is invisible unless a discovery protocol names it. This finds those hosts, at a price: every
    /// dead address in the range gets TCP connects too, so a large range means a great deal more traffic
    /// and a pattern that intrusion detection reads as a port scan. Sensible at home, to think about at
    /// work.</para></summary>
    public bool ScanUnpingableHosts { get; set; }

    /// <summary>Ceiling on TCP connects in flight during a scan. 0 = platform default (256, or 96 on
    /// macOS, whose per-process descriptor limit is far lower). Lower it on networks with fragile
    /// equipment or watchful intrusion detection.</summary>
    public int MaxConcurrentProbes { get; set; }

    /// <summary>Stop recording the privacy-scrubbed action log used for bug reports. Off by default (i.e.
    /// the log runs): it holds no credentials and pseudonymises addresses, and its whole point is to exist
    /// <i>before</i> the problem happens.</summary>
    public bool DisableActionLog { get; set; }

    /// <summary>Draw without the GPU (software renderer). Off by default – the GPU path is faster and right
    /// on healthy machines.
    /// <para>Exists because a broken or patched-in graphics driver can hang the whole window server when an
    /// app drives it: on an unsupported Mac running a newer macOS via OpenCore Legacy Patcher this shows up
    /// as the entire system freezing seconds after launch. Confirmed in the field, not theoretical.</para>
    /// <para>⚠️ Read once at startup when the renderer is chosen – changing it needs a restart.</para></summary>
    public bool SoftwareRendering { get; set; }

    /// <summary>Whether the device detail pane is shown, and how tall the user dragged it. Kept because a
    /// pane sized to one screen is wrong on the next start otherwise.</summary>
    public bool ShowDetailPane { get; set; } = true;
    public double DetailPaneHeight { get; set; } = 270;

    /// <summary>Hand the stored device password to an external client (WinSCP …) so the session opens
    /// without a prompt. <b>Off by default, deliberately:</b> the password travels as part of the session
    /// URL, which becomes the child process's command line – and command lines are readable by other
    /// processes on the machine (<c>ps</c> shows them to every local user on Linux, Process Explorer on
    /// Windows). Convenience worth having, but only when the user asks for it.</summary>
    public bool PassPasswordToExternalClients { get; set; }
    /// <summary>When true, the device list and its config (encrypted credentials included) are
    /// persisted to disk. Off by default – devices then only live for the current session.</summary>
    public bool PersistDeviceList { get; set; }
    /// <summary>Start the built-in web server automatically on launch. Off by default – the server is
    /// normally toggled on demand from the "Web server" menu.</summary>
    public bool WebServerAutoStart { get; set; }
    /// <summary>TCP port for the built-in web server (default 9090 – 80/8080 are usually already taken).</summary>
    public int WebServerPort { get; set; } = 9090;
    /// <summary>Username required to sign in to the web server (HTTP Basic auth). Defaults to "admin" so a
    /// user only has to set the password; a blank user (with a blank password) still blocks the server from
    /// starting.</summary>
    public string WebServerUser { get; set; } = "admin";
    /// <summary>Web-server password, DPAPI-encrypted (never stored in clear), like every other secret here.</summary>
    public string WebServerEncryptedPassword { get; set; } = "";
    /// <summary>Serve the web server over HTTPS (TLS). On by default – secure out of the box; required for
    /// any credential-carrying action anyway (passwords are never accepted over plain HTTP).</summary>
    public bool WebServerUseHttps { get; set; } = true;
    /// <summary>Path to the user's own certificate (.pfx/.p12) for HTTPS. Empty ⇒ a self-signed certificate
    /// is generated and cached automatically.</summary>
    public string WebServerCertPath { get; set; } = "";
    /// <summary>Password for the user's own .pfx, DPAPI-encrypted. Empty if the .pfx has none.</summary>
    public string WebServerCertPassword { get; set; } = "";
    /// <summary>Where the user dragged each topology node, so a rescan does not throw the arrangement
    /// away. Keyed by view + node key; a node that no longer exists is simply never looked up.</summary>
    public List<TopoNodePosition> TopoPositions { get; set; } = new();

    /// <summary>Nodes the user added by hand – gear TikMan cannot see (an unmanaged switch, a patch panel,
    /// the uplink to another site). ⚠️ Drawn dashed: these are assertions, not measurements, and the whole
    /// value of the physical map is that its solid lines are proven.</summary>
    public List<TopoManualNode> TopoManualNodes { get; set; } = new();

    /// <summary>Connections the user drew by hand. Same caveat as above – dashed, never merged into the
    /// evidence the map is built from.</summary>
    public List<TopoManualEdge> TopoManualEdges { get; set; } = new();

    /// <summary>Saved device-list column layout (order + width), only kept while
    /// <see cref="PersistDeviceList"/> is on. One entry per column in creation order.</summary>
    public List<ColumnState> ColumnLayout { get; set; } = new();

    /// <summary>Which generation of the column set a stored layout belongs to.
    ///
    /// <para>⚠️ Bumped whenever the columns change in a way that makes old pixel widths wrong – not just
    /// when their <i>number</i> changes. A layout is a list of pixel widths; restoring it pins every column,
    /// which silently defeats a later switch to content-based sizing. The count check alone cannot see that:
    /// swapping one column for another keeps the count identical while every width is now meaningless.</para></summary>
    public int ColumnLayoutVersion { get; set; }
    /// <summary>Sorted column (index in creation order, -1 = none) and its direction.</summary>
    public int SortColumn { get; set; } = -1;
    public bool SortDescending { get; set; }

    /// <summary>The same four values for the IPv6 view. ⚠️ Its own slot, not shared: the two grids have
    /// different columns, so one stored layout applied to both would hand each of them the other's widths
    /// and sort field.</summary>
    public List<ColumnState> Ipv6ColumnLayout { get; set; } = new();
    public int Ipv6ColumnLayoutVersion { get; set; }
    public int Ipv6SortColumn { get; set; } = -1;
    public bool Ipv6SortDescending { get; set; }
    public List<Device> Devices { get; set; } = new();
}

/// <summary>Persisted layout of one device-list column.</summary>
public class ColumnState
{
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
}

/// <summary>Where one topology node sits, per view. <paramref name="View"/> is "logical" or "physical" –
/// the same device has different keys and a different place in each, so they are stored separately.</summary>
public class TopoNodePosition
{
    public string View { get; set; } = "";
    public string Key { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A node the user added by hand. <see cref="Key"/> is generated once and kept, so edges can
/// refer to it across restarts.</summary>
public class TopoManualNode
{
    public string View { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A connection the user drew. Endpoints are node keys – either generated ones (manual nodes) or
/// the keys the topology builder assigns to real devices.</summary>
public class TopoManualEdge
{
    public string View { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>Loads/saves the app data as JSON under %AppData%\TikMan.</summary>
public static class DeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }, // store enums as readable strings
    };

    /// <summary>The per-user settings folder (…/TikMan). On Windows that is %AppData%\TikMan.
    /// <para>⚠️ On Linux/macOS single-file builds <see cref="Environment.GetFolderPath"/> can hand back
    /// an empty string – which would put the settings in a stray relative "TikMan" beside the working
    /// directory (a different place every time the app is launched from a different folder). So when it
    /// comes back empty we resolve the platform's config home ourselves: <c>$XDG_CONFIG_HOME</c> or
    /// <c>$HOME/.config</c> on Linux, <c>$HOME/Library/Application Support</c> on macOS – never "".</para></summary>
    public static string StorageDirectory
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = FallbackConfigHome();
            return Path.Combine(baseDir, "TikMan");
        }
    }

    private static string FallbackConfigHome()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (OperatingSystem.IsMacOS() && !string.IsNullOrEmpty(home))
            return Path.Combine(home, "Library", "Application Support");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg)) return xdg;
        if (!string.IsNullOrEmpty(home)) return Path.Combine(home, ".config");
        return Path.Combine(Path.GetTempPath(), "tikman-config"); // last resort – absolute, never ""
    }

    /// <summary>The settings file. Holds the whole <see cref="AppData"/> – every preference, and the device
    /// list only when the user asked for it to be kept.</summary>
    public static string StorageFile => Path.Combine(StorageDirectory, "settings.json");

    /// <summary>What the settings file was called until 2.2.2, back when the device list was all it held.
    /// The name outlived its accuracy: it now carries roughly eighty settings, and with «keep device list»
    /// turned off it contains no devices at all.</summary>
    private static string LegacyStorageFile => Path.Combine(StorageDirectory, "devices.json");

    /// <summary>Moves a pre-2.2.2 config to the new name, once.
    ///
    /// <para>⚠️ Move rather than copy, and only when the new name does not exist: leaving the old file behind
    /// would mean two configs that silently diverge – whichever one a given build reads wins, and settings
    /// appear to "not save" depending on which client was opened last.</para></summary>
    private static void MigrateLegacyName()
    {
        try
        {
            if (File.Exists(StorageFile) || !File.Exists(LegacyStorageFile)) return;
            File.Move(LegacyStorageFile, StorageFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't rename (file in use, read-only volume): Load falls back to the old name below, so the
            // user keeps their settings either way and the rename retries on the next start.
        }
    }

    public static AppData Load()
    {
        MigrateLegacyName();
        // If the rename could not happen, read the old file where it lies rather than starting blank –
        // a failed migration must never look like "all your settings are gone".
        var source = File.Exists(StorageFile) ? StorageFile
            : File.Exists(LegacyStorageFile) ? LegacyStorageFile : StorageFile;
        try
        {
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                // Must use JsonOptions here too: the enums (Language/BackupMethod) are stored as strings,
                // and without the JsonStringEnumConverter deserialisation throws – which used to be swallowed
                // as "corrupt" and silently reset every setting to its default (settings never persisted).
                var data = JsonSerializer.Deserialize<AppData>(text, JsonOptions) ?? new AppData();
                // Grandfather existing users: a config written before the persistence toggle existed
                // and that already holds devices must keep persisting them – otherwise upgrading to a
                // build with the (default-off) toggle would silently wipe the device list on the next save.
                if (data.Devices.Count > 0 && !text.Contains("\"PersistDeviceList\"", StringComparison.Ordinal))
                    data.PersistDeviceList = true;
                return data;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // don't overwrite a corrupt file, set it aside instead (keep the first one if it exists)
            try
            {
                var backup = source + ".corrupt";
                if (!File.Exists(backup)) File.Move(source, backup);
            }
            catch { }
        }
        return new AppData();
    }

    /// <summary>⚠️ Serialises every write. The atomic temp-file+move below is only atomic against a
    /// <i>reader</i> – two concurrent writers share the same "<c>settings.json.tmp</c>" path, so without this
    /// lock they interleave into one temp file and then both move it over the real one. That happens for
    /// real: the GUI saves on the UI thread (a theme switch is enough) while a scan thread persists the
    /// device list. The file being clobbered is the credential store, so this is not a theoretical race.</summary>
    private static readonly object SaveLock = new();

    public static void Save(AppData data)
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(StorageDirectory);
            var tmp = StorageFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(tmp, StorageFile, overwrite: true);
        }
    }

    /// <summary>Deletes the stored config so the next start is like a first run.</summary>
    public static void DeleteConfig()
    {
        foreach (var path in new[]
                 {
                     StorageFile, StorageFile + ".tmp", StorageFile + ".corrupt",
                     LegacyStorageFile, LegacyStorageFile + ".tmp", LegacyStorageFile + ".corrupt",
                 })
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    /// <summary>Everything the app has ever written into its config folder, in the order it is deleted.
    ///
    /// <para>⚠️ Deliberately a fixed list rather than "delete the folder": the folder also holds
    /// <c>oui.txt</c>, a 4 MB vendor database the user downloaded on purpose. It contains nothing personal
    /// and re-fetching it needs the internet, so a reset keeps it. Wiping a directory wholesale would take
    /// it – and anything a future version puts there – along without anyone deciding that.</para></summary>
    private static readonly string[] ResetFiles =
    {
        "settings.json", "settings.json.tmp", "settings.json.corrupt",
        // ⚠️ The pre-2.2.2 name too: a reset that leaves it behind is not a reset – the very next start
        // migrates it straight back and the devices reappear.
        "devices.json", "devices.json.tmp", "devices.json.corrupt",
        "webserver.pfx",        // cached self-signed certificate
        "credential.key",       // the AES key protecting stored passwords on Unix
        "tikman-login.txt",     // first-run web password handed to the headless host
        "tikman-actions.log", "crash.log",
    };

    /// <summary>Returns the app to its factory state: no devices, no stored logins, no certificate.
    ///
    /// <para>⚠️ The caller <b>must not keep running</b> afterwards. The live <see cref="AppData"/> is still in
    /// memory and the next save – a finished scan is enough – writes the whole thing back, so a reset that
    /// leaves the app open quietly undoes itself.</para></summary>
    /// <returns>The names of the files that were actually removed, so the UI can say what happened.</returns>
    public static IReadOnlyList<string> ResetToFactoryState()
    {
        var removed = new List<string>();
        foreach (var name in ResetFiles)
        {
            var path = Path.Combine(StorageDirectory, name);
            // Deleting the key before the file it protects would leave unreadable passwords behind if the
            // wipe failed halfway; the order above puts the config files first for exactly that reason.
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file held open by another instance stays; reporting the rest beats failing the lot.
            }
        }
        // The key file is gone; the copy this process cached must go too, or anything encrypted between
        // here and the restart would be unreadable afterwards.
        CredentialProtector.ForgetCachedKey();
        return removed;
    }
}
