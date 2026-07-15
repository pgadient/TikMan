namespace TikMan.App.Web;

/// <summary>A read-only snapshot of one device for the web dashboard. Deliberately flat and free of
/// any WPF/ViewModel type so it can be serialised straight to JSON off the UI thread's data.
/// Never carries credentials – the web layer must never hand out stored passwords.</summary>
public sealed record DeviceDto(
    string Name,
    string Ip,
    string Mac,
    string Vendor,
    string Type,
    string Model,
    string Status,
    bool IsGateway,
    bool HasLogin);

/// <summary>What the web server needs from the running app. Implemented by the main window, which owns
/// the live device list; every call marshals onto the UI thread so the web threads never touch WPF
/// state directly. Kept as an interface so the web layer has no dependency on the window itself.</summary>
public interface IWebBackend
{
    /// <summary>A snapshot of the current device list (as shown in the GUI right now).</summary>
    IReadOnlyList<DeviceDto> GetDevices();

    /// <summary>Product name + version for the page header.</summary>
    string AppTitle { get; }
    string AppVersion { get; }
}
