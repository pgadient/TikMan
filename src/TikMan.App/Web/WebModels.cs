namespace TikMan.App.Web;

/// <summary>A read-only snapshot of one device for the web dashboard. Deliberately flat and free of
/// any WPF/ViewModel type so it can be serialised straight to JSON off the UI thread's data.
/// Never carries credentials – the web layer must never hand out stored passwords.</summary>
public sealed record DeviceDto(
    string Id,
    string Name,
    string Ip,
    string Mac,
    string Vendor,
    string Type,
    string Model,
    string Status,
    bool IsGateway,
    bool HasLogin);

/// <summary>One label/value line for the device-detail panel (the same rows the GUI shows).</summary>
public sealed record KeyVal(string Key, string Value);

/// <summary>Everything the detail panel shows for one device: the summary fields, all IPv6 addresses,
/// and the free-form info rows. Carries whether a Wake-on-LAN is possible, never a password.</summary>
public sealed record DeviceDetail(
    string Id, string Name, string Ip, string Mac, string Vendor, string Type, string Model,
    string Status, bool HasLogin, string User, bool CanWake, IReadOnlyList<string> Ipv6, IReadOnlyList<KeyVal> Info);

/// <summary>Result of a web-triggered action (Wake …), for a small toast in the browser.</summary>
public sealed record ActionResult(bool Ok, string Message);

/// <summary>One box in the topology map: its laid-out position/size and the same colours + text the
/// GUI draws. <see cref="DeviceId"/> is set for real devices (empty for the "Internet"/range pseudo-
/// nodes), so a click can open the device's detail panel. Colours are CSS-ready hex strings.</summary>
public sealed record TopoNodeDto(string Key, string DeviceId, string Title, string Detail, string Mac,
    double X, double Y, double W, double H, string Fill, string Line, string Text);

public sealed record TopoEdgeDto(string From, string To);

/// <summary>The laid-out topology graph (logical or physical) for the browser to render as SVG.</summary>
public sealed record TopoGraph(IReadOnlyList<TopoNodeDto> Nodes, IReadOnlyList<TopoEdgeDto> Edges);

/// <summary>A backup ready to stream to the browser as a download, or a failure with a reason.
/// The bytes are the file content; they are never logged.</summary>
public sealed record BackupResult(bool Ok, string Message, string FileName, string ContentType, byte[] Bytes)
{
    public static BackupResult Fail(string message) => new(false, message, "", "", Array.Empty<byte>());
}

/// <summary>Live scan state for the dashboard's progress indicator. <see cref="Progress"/> is 0..1, or
/// -1 when the current phase is indeterminate; both are only meaningful while <see cref="Scanning"/>.</summary>
public sealed record WebStatus(bool Scanning, double Progress, string Phase, int DeviceCount);

/// <summary>What the web server needs from the running app. Implemented by the main window, which owns
/// the live device list; every call marshals onto the UI thread so the web threads never touch WPF
/// state directly. Kept as an interface so the web layer has no dependency on the window itself.</summary>
public interface IWebBackend
{
    /// <summary>A snapshot of the current device list (as shown in the GUI right now).</summary>
    IReadOnlyList<DeviceDto> GetDevices();

    /// <summary>Full detail for one device by its <see cref="DeviceDto.Id"/>, or null if it's gone.</summary>
    DeviceDetail? GetDevice(string id);

    /// <summary>Sends a Wake-on-LAN magic packet to the device with this id.</summary>
    ActionResult Wake(string id);

    /// <summary>Sets (or clears) the device's login, exactly like the GUI's "set credentials". The
    /// password is used only to DPAPI-encrypt it into the device; it is never logged. The web server
    /// only ever calls this over HTTPS.</summary>
    ActionResult SetLogin(string id, string user, string password);

    /// <summary>Produces a backup of the device: the config export (.rsc, text) or the full binary
    /// backup (.backup) when <paramref name="full"/>. Needs the device's stored login. The web server
    /// only ever calls this over HTTPS (the backup can contain secrets).</summary>
    Task<BackupResult> MakeBackupAsync(string id, bool full);

    /// <summary>Builds and returns the topology map (physical = forwarding-table based, else the logical
    /// address-distribution view) as laid-out nodes + edges. Slow the first time a physical view gathers
    /// forwarding tables.</summary>
    Task<TopoGraph> GetTopologyAsync(bool physical);

    /// <summary>Current scan state (running? how far? which phase? how many devices).</summary>
    WebStatus GetStatus();

    /// <summary>Starts a discovery scan if one isn't already running (same as the GUI's Scan button).</summary>
    void StartScan();

    /// <summary>Product name + version for the page header.</summary>
    string AppTitle { get; }
    string AppVersion { get; }
}
