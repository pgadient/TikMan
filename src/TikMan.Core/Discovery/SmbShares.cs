using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TikMan.Core.Discovery;

/// <summary>Outcome of a share enumeration: shares came back, the server refused us (needs a
/// login / admin rights), or it was unreachable.</summary>
public enum ShareListStatus { Ok, AccessDenied, Failed }

public readonly record struct ShareListResult(ShareListStatus Status, List<string> Shares);

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
    private static readonly string[] ProbeShares = { "print$" };

    private const uint StypeSpecial = 0x80000000; // admin$, C$, IPC$ …
    private const uint StypeBaseMask = 0x0F;
    private const uint StypeDisktree = 0;
    private const int MaxPreferredLength = -1;

    /// <summary>Returns the visible disk-share names of a host and how the enumeration went.
    /// NOTE: NetShareEnum is a blocking native call that ignores cancellation – the caller should
    /// race it against a timeout so the UI never hangs on a slow/unresponsive server.</summary>
    public static Task<ShareListResult> ListAsync(string host, CancellationToken ct = default) =>
        Task.Run(() =>
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

            // Probe well-known hidden shares (print$) by name – NetShareEnum only lists those to admins.
            foreach (var name in ProbeShares)
                if (!seen.Contains(name) && ShareExists(host, name)) { shares.Add(name); seen.Add(name); }

            if (shares.Count > 0) return new ShareListResult(ShareListStatus.Ok, shares);
            if (denied) return new ShareListResult(ShareListStatus.AccessDenied, shares);
            return new ShareListResult(ShareListStatus.Failed, shares);
        }, ct);

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
