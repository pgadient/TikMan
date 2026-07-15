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
    string Status, bool HasLogin, bool CanWake, IReadOnlyList<string> Ipv6, IReadOnlyList<KeyVal> Info);

/// <summary>Result of a web-triggered action (Wake …), for a small toast in the browser.</summary>
public sealed record ActionResult(bool Ok, string Message);

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

    /// <summary>Current scan state (running? how far? which phase? how many devices).</summary>
    WebStatus GetStatus();

    /// <summary>Starts a discovery scan if one isn't already running (same as the GUI's Scan button).</summary>
    void StartScan();

    /// <summary>Product name + version for the page header.</summary>
    string AppTitle { get; }
    string AppVersion { get; }
}
