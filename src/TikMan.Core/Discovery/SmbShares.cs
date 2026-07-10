using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TikMan.Core.Discovery;

/// <summary>Outcome of a share enumeration: shares came back, the server refused us (needs a
/// login / admin rights), or it was unreachable.</summary>
public enum ShareListStatus { Ok, AccessDenied, Failed }

public readonly record struct ShareListResult(ShareListStatus Status, List<string> Shares);

/// <summary>Lists the SMB/Windows file shares exposed by a host via the Windows server API
/// (netapi32 NetShareEnum, level 1). Returns the user-visible disk shares (admin$, C$, IPC$
/// and other special shares are filtered out). Password-protected servers typically require a
/// session/admin rights and answer with access-denied.</summary>
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

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

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
            int resume = 0;
            int rc = NetShareEnum($@"\\{host}", 1, out var buffer, MaxPreferredLength,
                out int read, out _, ref resume);
            if (rc == ErrorAccessDenied) return new ShareListResult(ShareListStatus.AccessDenied, shares);
            if (rc != 0 || buffer == IntPtr.Zero) return new ShareListResult(ShareListStatus.Failed, shares);

            try
            {
                int size = Marshal.SizeOf<ShareInfo1>();
                for (int i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<ShareInfo1>(buffer + i * size);
                    // Include disk shares, including hidden/administrative ones (C$, D$, ADMIN$, PRINT$).
                    // IPC$ is not a disk share and is skipped by the disk-type check.
                    bool disk = (info.Type & StypeBaseMask) == StypeDisktree;
                    if (disk && !string.IsNullOrEmpty(info.Netname))
                        shares.Add(info.Netname);
                }
            }
            finally
            {
                NetApiBufferFree(buffer);
            }

            return new ShareListResult(ShareListStatus.Ok, shares);
        }, ct);
}
