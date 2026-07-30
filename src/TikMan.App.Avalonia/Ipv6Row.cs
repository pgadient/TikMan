using System;
using System.Collections.Generic;
using System.ComponentModel;
using TikMan.Core.Discovery;
using TikMan.Core.Fleet;

namespace TikMan.App.Avalonia;

/// <summary>One row of the IPv6 view: a <b>single</b> IPv6 address of a device.
///
/// <para>This is what the IPv6 tab is for. A device commonly has four or five addresses at once – a global
/// one, a ULA, a link-local, one or two privacy addresses – and each behaves differently: one is routable
/// from outside, one only on this segment, one rotates every few hours. Summarising them into a single cell
/// (what this view did before) hides exactly the distinction the user opened the tab to see, and makes an
/// individual address impossible to sort by, filter for or copy.</para>
///
/// <para>Device facts are delegated to the underlying snapshot, so the shared columns bind the same way as
/// in the IPv4 list. Rows of one device carry the same <see cref="Group"/> number and background, so they
/// stay visually together and a single click on the group header restores the grouping after any other
/// sort.</para></summary>
public sealed class Ipv6Row : INotifyPropertyChanged, IExpandableRow
{
    public DeviceSnapshot Device { get; }

    /// <summary>The one address this row stands for.</summary>
    public string Address { get; }

    /// <summary>Scope badge for this address (global / ULA / link-local / …) – per address, because that is
    /// precisely what differs between the rows of one device.</summary>
    public Ipv6Tag Tag { get; }

    /// <summary>1-based device number. Shared by all rows of a device, so sorting on it re-groups them.</summary>
    public int Group { get; }

    /// <summary>True on a device's first row. Kept for callers that want to draw a separator between
    /// devices; the columns themselves no longer depend on it.</summary>
    public bool IsFirstOfDevice { get; }

    /// <summary>Sort key for the group column: device first, then the address, so one click restores
    /// "grouped by device, addresses in order".</summary>
    public string GroupSortKey => $"{Group:D5} {Address}";

    /// <summary>Alternating tint per device – the cheapest way to see where one device's block ends.</summary>
    public string RowBackground { get; }

    /// <summary>What talking to <b>this address</b> revealed. Empty fields mean "this address did not say",
    /// and the columns then fall back to the device-level fact.</summary>
    public Ipv6Facts Facts { get; }

    public Ipv6Row(DeviceSnapshot device, Ipv6Entry entry, int group, bool firstOfDevice)
    {
        Device = device;
        Address = entry.Address;
        Tag = entry.Tag;
        Badges = entry.Badges;
        Probed = entry.Probed;
        Facts = entry.Facts;
        Group = group;
        IsFirstOfDevice = firstOfDevice;
        // Very low alpha: it has to read as "same block", not as a highlight competing with the row states.
        RowBackground = group % 2 == 0 ? "#0D4A90E2" : "#00000000";
    }

    // --- device facts, on EVERY row --------------------------------------------------------------------
    // ⚠️ Repeated deliberately, not only on the device's first row. A row here is a self-contained statement
    // about one address, and it has to survive being sorted, filtered or exported away from its siblings –
    // at which point a row showing only an address with every other cell blank is unreadable. It makes the
    // list denser; the group tint is what carries "these belong together".
    //
    // ⚠️ Where a fact can be established by talking to an address, the ADDRESS's answer wins and the device
    // value is only the fallback (Prefer below). That is what makes this more than a re-shaped IPv4 list: a
    // dual-stack host can serve a different name, OS or web UI over v6, and copying the v4 answer into every
    // row would make an address that answers nothing look exactly like one that answers fully.
    // Facts nothing can measure per address (MAC, OUI, monitoring counters, update state) stay device-level.
    private static string Prefer(string perAddress, string device) =>
        perAddress.Length > 0 ? perAddress : device;

    public string Name => Prefer(Facts.Name, Device.Name);
    public string KindText => Device.KindText;
    public string Ip => Device.Ip;
    public string Mac => Device.Mac;
    public string MacVendor => Device.MacVendor;
    public string Vendor => Prefer(Facts.Vendor, Device.Vendor);
    public string Model => Prefer(Facts.Model, Device.Model);
    public string Serial => Prefer(Facts.Serial, Device.Serial);
    public string Os => Prefer(Facts.Os, Device.Os);
    public string Firmware => Device.Firmware;
    public string LatestVersion => Device.LatestVersion;
    // Latest-firmware link/display: device-level (the vendor page is about the device, not one address).
    public string LatestDisplay => Device.LatestDisplay;
    public string LatestVersionUrl => Device.LatestVersionUrl;
    public bool HasLatestLink => Device.HasLatestLink;
    public bool UpdateAvailable => Device.UpdateAvailable;
    public string InstalledRelease => Device.InstalledRelease;
    public string UpdateRelease => Device.UpdateRelease;
    public string Cpu => Device.Cpu;
    public string Memory => Device.Memory;
    public string Uptime => Device.Uptime;
    public string Status => Device.Status;

    /// <summary>The shares this <b>address</b> served, space-joined for a cell. "denied" when the host
    /// refused to list them – an empty cell would read as "TikMan never asked".</summary>
    public string SharesText =>
        Facts.HasShares ? string.Join(" ", Facts.ShareNames)
        : Facts.SharesDenied ? "denied"
        : "";

    /// <summary>The services that answered <b>on this address</b> – measured, not copied from the device's
    /// IPv4 result. Its links open over this address.</summary>
    public IReadOnlyList<ServiceBadge> Badges { get; }

    /// <summary>Whether this address was actually tested. Separates "tested, answers nothing" from "not
    /// tested" (link-local, or no scan yet) – the row says which, instead of leaving both blank.</summary>
    public bool Probed { get; }

    /// <summary>Shown when the address was probed and answered nothing at all.</summary>
    public bool NothingAnswered => Probed && Badges.Count == 0;

    /// <summary>Shown when the address was never dialled – link-local needs an interface zone index that
    /// the neighbour cache does not carry here.</summary>
    public bool NotProbed => !Probed;
    public bool HasLogin => Device.HasLogin;
    public bool IsGateway => Device.IsGateway;
    public bool IsOffline => Device.IsOffline;

    // --- expandable row details (shares for THIS address) ----------------------------------------------
    // ⚠️ The shares shown when the row is expanded are the ones THIS address served (Facts.ShareLinks),
    // not the device's IPv4 result – the same per-address principle as every other column here.
    public bool HasRowDetails => Facts.HasShares || Facts.SharesDenied;

    /// <summary>UI state: is this address row's detail panel open? Per row (not per device) on purpose – a
    /// device has several address rows, and expanding one should not fan the same panel open under all of
    /// them. Mutable and observable so the grid can bind a per-row expander to it.</summary>
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
