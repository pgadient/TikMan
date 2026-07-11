using System.ComponentModel;
using System.Windows.Media;

namespace TikMan.App;

/// <summary>One row of the IPv6 view: a single IPv6 address of a device. All device facts are
/// delegated to the underlying DeviceViewModel (same property names, so the shared columns bind);
/// rows of the same device share a group colour so they visually stick together.</summary>
public class Ipv6RowVm : INotifyPropertyChanged
{
    public DeviceViewModel Device { get; }

    /// <summary>The one IPv6 address this row stands for (fills the IPv6 column).</summary>
    public string Ipv6Summary { get; }

    /// <summary>True on a device's first row – only that row carries the SMB expander.</summary>
    public bool IsFirstOfDevice { get; }

    /// <summary>Alternating white/ice-blue per device.</summary>
    public Brush RowBackground { get; }

    public Ipv6RowVm(DeviceViewModel device, string ipv6, bool firstOfDevice, Brush background)
    {
        Device = device;
        Ipv6Summary = ipv6;
        IsFirstOfDevice = firstOfDevice;
        RowBackground = background;
        // Property names match, so the device's change notifications drive this row's bindings too.
        device.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isExpanded;
    /// <summary>Expands the SMB row details (shares load lazily on first expand).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            if (value) _ = Device.LoadSharesAsync();
        }
    }

    /// <summary>The expander only carries SMB shares here – every address already has its own row.</summary>
    public bool HasRowDetails => IsFirstOfDevice && Device.HasSmb;

    /// <summary>Nothing extra to list in the details (see HasRowDetails).</summary>
    public IReadOnlyList<string> Ipv6Rest => Array.Empty<string>();

    /// <summary>Sorting the IPv6 column sorts by the row's own address.</summary>
    public string Ipv6Display => Ipv6Summary;

    // --- delegated to the device (one-way; declared as object so the runtime type decides) ---
    public bool IsSelected { get => Device.IsSelected; set => Device.IsSelected = value; }
    public object Model => Device.Model;
    public object StatusBrush => Device.StatusBrush;
    public object StatusText => Device.StatusText;
    public object DeviceType => Device.DeviceType;
    public object Name => Device.Name;
    public object Ipv4Address => Device.Ipv4Address;
    public object Ipv4SortKey => Device.Ipv4SortKey;
    public object SupportedProtocols => Device.SupportedProtocols;
    public object TransportDisplay => Device.TransportDisplay;
    public object MacVendor => Device.MacVendor;
    public object IdentifiedVendor => Device.IdentifiedVendor;
    public object ModelDisplay => Device.ModelDisplay;
    public object Version => Device.Version;
    public object UpdateAvailable => Device.UpdateAvailable;
    public object VersionIsCurrent => Device.VersionIsCurrent;
    public object LatestWithChannel => Device.LatestWithChannel;
    public object InstalledReleaseText => Device.InstalledReleaseText;
    public object UpdateReleaseText => Device.UpdateReleaseText;
    public object CpuText => Device.CpuText;
    public object MemoryText => Device.MemoryText;
    public object Uptime => Device.Uptime;
    public object IsGateway => Device.IsGateway;
    public object IsOffline => Device.IsOffline;
    public object HasSmb => Device.HasSmb;
    public object SharesStatus => Device.SharesStatus;
    public object Shares => Device.Shares;
}
