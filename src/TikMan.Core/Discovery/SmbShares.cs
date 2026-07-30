using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TikMan.Core.Discovery;

/// <summary>Outcome of a share enumeration: shares came back, the server refused us (needs a
/// login / admin rights), or it was unreachable.</summary>
public enum ShareListStatus { Ok, AccessDenied, Failed }

/// <param name="Host">The name (or address) the enumeration actually succeeded with. ⚠️ Worth carrying:
/// a session is keyed to the server NAME, so the UNC paths handed to Explorer have to use the same form
/// that worked – opening <c>\\10.0.0.5\share</c> after listing via <c>\\fileserv</c> re-authenticates from
/// scratch and can prompt or fail.</param>
public readonly record struct ShareListResult(ShareListStatus Status, List<string> Shares, string Host = "");

/// <summary>Lists the SMB/Windows disk shares exposed by a host via the Windows server API
/// (netapi32 NetShareEnum, level 1) – including hidden/administrative disk shares (C$, ADMIN$,
/// print$) when the caller has the rights to see them. Well-known hidden shares that a non-admin
/// enumeration won't list (print$) are additionally probed by name. IPC$ (not a disk share) is
/// skipped. Password-protected servers may require a session/admin rights and answer access-denied.</summary>
[SupportedOSPlatform("windows")]
public static class SmbShares
{
    private const int ErrorAccessDenied = 5; // ERROR_ACCESS_DENIED
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        public string Netname;
        public uint Type;
        public string Remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string? serverName, int level, out IntPtr bufPtr, int prefMaxLen,
        out int entriesRead, out int totalEntries, ref int resumeHandle);

    // Level 1 needs no admin rights, so it detects a share's existence even when NetShareEnum (which
    // only lists hidden shares like print$ to admins) doesn't return it.
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareGetInfo(string? serverName, string netName, int level, out IntPtr bufPtr);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    // Hidden shares worth surfacing that a non-admin NetShareEnum won't list (print$ = a print server).
    private static readonly string[] ProbeShares = { "print$", "ADMIN$" };

    /// <summary>Every drive letter as an administrative share (A$ … Z$), probed by name.
    ///
    /// <para>⚠️ Only used when the enumeration could not list them itself – see the call site. Each name is a
    /// separate round trip to the host, so probing all 26 unconditionally would add 26 of them per device to
    /// every scan, for a list the enumeration usually already returned.</para></summary>
    private static readonly string[] DriveShares =
        Enumerable.Range('A', 26).Select(c => $"{(char)c}$").ToArray();

    private const uint StypeSpecial = 0x80000000; // admin$, C$, IPC$ …
    private const uint StypeBaseMask = 0x0F;
    private const uint StypeDisktree = 0;
    private const int MaxPreferredLength = -1;

    /// <summary>Returns the visible disk-share names of a host and how the enumeration went.
    /// NOTE: NetShareEnum is a blocking native call that ignores cancellation – the caller should
    /// race it against a timeout so the UI never hangs on a slow/unresponsive server.</summary>
    public static Task<ShareListResult> ListAsync(string host, CancellationToken ct = default)
    {
        // netapi32 only exists on Windows; elsewhere share enumeration is simply unavailable
        // (Failed, like an unreachable server – the UI already treats that as "nothing to show").
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new ShareListResult(ShareListStatus.Failed, new List<string>()));
        return Task.Run(() =>
        {
            var shares = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int resume = 0;
            int rc = NetShareEnum($@"\\{host}", 1, out var buffer, MaxPreferredLength,
                out int read, out _, ref resume);
            bool denied = rc == ErrorAccessDenied;

            if (rc == 0 && buffer != IntPtr.Zero)
            {
                try
                {
                    int size = Marshal.SizeOf<ShareInfo1>();
                    for (int i = 0; i < read; i++)
                    {
                        var info = Marshal.PtrToStructure<ShareInfo1>(buffer + i * size);
                        // Include disk shares, including hidden/administrative ones (C$, D$, ADMIN$, PRINT$).
                        // IPC$ is not a disk share and is skipped by the disk-type check.
                        bool disk = (info.Type & StypeBaseMask) == StypeDisktree;
                        if (disk && !string.IsNullOrEmpty(info.Netname) && seen.Add(info.Netname))
                            shares.Add(info.Netname);
                    }
                }
                finally { NetApiBufferFree(buffer); }
            }

            // Probe well-known hidden shares by name – NetShareEnum only lists those to admins.
            foreach (var name in ProbeShares)
                if (!seen.Contains(name) && ShareExists(host, name)) { shares.Add(name); seen.Add(name); }

            // The drive letters, by name.
            //
            // ⚠️ Two guards, both measured rather than assumed:
            // 1. Only when the enumeration produced no drive share of its own. With admin rights it lists
            //    C$/D$/… already, and re-asking for all 26 would cost a round trip each, per device, per
            //    scan, for names we have.
            // 2. Only when the host actually answered (listed something, or refused). NetShareGetInfo is a
            //    blocking native call that ignores the token, and against a host that is simply not there
            //    each one runs into its own RPC timeout – 26 of those per device stalls a scan outright.
            //    An unreachable host has no shares to find, so there is nothing lost by not asking.
            // 3. NOT when the enumeration was denied. Anonymous enumeration being refused (a locked-down
            //    server, e.g. RestrictNullSessAccess) means the admin drive shares are refused too – so 26
            //    per-name probes would each run into the same slow deny. Measured against a routed server:
            //    NetShareEnum alone took 47 s to return access-denied; the drive loop then piled on more.
            //    Those hosts need a stored login to list anything, and that is handled elsewhere.
            bool responded = rc == 0;
            if (responded && !shares.Any(IsDriveShare))
                foreach (var name in DriveShares)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!seen.Contains(name) && ShareExists(host, name)) { shares.Add(name); seen.Add(name); }
                }

            if (shares.Count > 0) return new ShareListResult(ShareListStatus.Ok, shares);
            if (denied) return new ShareListResult(ShareListStatus.AccessDenied, shares);
            return new ShareListResult(ShareListStatus.Failed, shares);
        }, ct);
    }

    /// <summary>A single-letter administrative share, "C$" and friends.</summary>
    private static bool IsDriveShare(string name) =>
        name.Length == 2 && char.IsAsciiLetter(name[0]) && name[1] == '$';

    /// <summary>Lists a host's shares, trying its <b>names</b> before its address.
    ///
    /// <para>⚠️ This is the part that decides whether shares appear at all, and it is not cosmetic.
    /// Enumeration runs under the calling user's Windows identity, and Windows keys an existing session
    /// (mapped drive, stored credential, Kerberos ticket) to the server <i>name</i>. Asked by name a
    /// server answers in a second or two; asked by bare IP the very same server negotiates from scratch
    /// and – measured against a routed file server – took <b>47 seconds to return access-denied</b>. The
    /// IP-only path is why the shares silently never showed up.</para>
    ///
    /// <para>Each candidate gets its own short timeout: a blocked one must not eat the budget of the name
    /// that would have worked. The first candidate that returns shares wins; otherwise the worst outcome
    /// seen is reported, so the UI can say "denied" rather than showing nothing at all.</para></summary>
    public static async Task<ShareListResult> ListForHostAsync(
        string ip, string hostName = "", CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new ShareListResult(ShareListStatus.Failed, new List<string>());

        var candidates = new List<string>();
        void Add(string? c)
        {
            if (c is { Length: > 0 } && !candidates.Contains(c, StringComparer.OrdinalIgnoreCase))
                candidates.Add(c);
        }

        // A name TikMan already knows (from SMB/mDNS/SNMP) – but only if it is not itself an address.
        if (!System.Net.IPAddress.TryParse(hostName, out _)) Add(hostName);
        try
        {
            var dns = (await System.Net.Dns.GetHostEntryAsync(ip, ct).ConfigureAwait(false)).HostName;
            Add(dns);
            Add(dns.Split('.')[0]);   // the short/NetBIOS form, which is what a session is usually keyed to
        }
        catch { /* no PTR record – the address below still gets its turn */ }
        Add(UncHost(ip));

        var denied = false;
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var listing = ListAsync(candidate, ct);
                // ⚠️ Raced, not awaited: NetShareEnum is a blocking native call that ignores the token, so
                // the only way past a stalled host is to stop waiting for it. The abandoned call finishes
                // on its own thread and is simply not read.
                if (await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(5), ct))
                        .ConfigureAwait(false) != listing) continue;

                var result = await listing.ConfigureAwait(false);
                if (result.Status == ShareListStatus.Ok && result.Shares.Count > 0)
                    return result with { Host = candidate };
                denied |= result.Status == ShareListStatus.AccessDenied;
            }
            catch { /* this candidate is a dead end; the next one may not be */ }
        }

        return new ShareListResult(denied ? ShareListStatus.AccessDenied : ShareListStatus.Failed,
            new List<string>());
    }

    /// <summary>The host as it may appear in a UNC path.
    ///
    /// <para>⚠️ A raw IPv6 literal cannot: <c>\\fe80::1\c$</c> is not a legal path – the colon is the drive
    /// separator, so Windows rejects the whole thing and the enumeration fails before a packet is sent.
    /// The transport is the <c>ipv6-literal.net</c> form: colons become dashes, the zone's <c>%</c> becomes
    /// an <c>s</c>, and <c>.ipv6-literal.net</c> is appended. The SMB client resolves that shape itself; it
    /// never goes to DNS. IPv4 and real names are returned unchanged.</para></summary>
    public static string UncHost(string host)
    {
        if (!System.Net.IPAddress.TryParse(host, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return host;
        return host.Replace(':', '-').Replace("%", "s") + ".ipv6-literal.net";
    }

    /// <summary>True when the host exposes a disk share of the given name (NetShareGetInfo level 1 needs
    /// no admin rights: it returns the share for anyone, or NERR_NetNameNotFound when it doesn't exist).</summary>
    private static bool ShareExists(string host, string name)
    {
        if (NetShareGetInfo($@"\\{host}", name, 1, out var buf) != 0 || buf == IntPtr.Zero) return false;
        try
        {
            var info = Marshal.PtrToStructure<ShareInfo1>(buf);
            return (info.Type & StypeBaseMask) == StypeDisktree;
        }
        finally { NetApiBufferFree(buf); }
    }
}
