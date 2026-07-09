using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TikMan.Core.Discovery;

/// <summary>Lists the SMB/Windows file shares exposed by a host via the Windows server API
/// (netapi32 NetShareEnum, level 1). Returns the user-visible disk shares (admin$, C$, IPC$
/// and other special shares are filtered out). Requires no admin rights for level 1.</summary>
[SupportedOSPlatform("windows")]
public static class SmbShares
{
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

    /// <summary>Returns the visible disk-share names of a host (empty if none / not permitted).</summary>
    public static Task<List<string>> ListAsync(string host, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var shares = new List<string>();
            int resume = 0;
            int rc = NetShareEnum($@"\\{host}", 1, out var buffer, MaxPreferredLength,
                out int read, out _, ref resume);
            if (rc != 0 || buffer == IntPtr.Zero) return shares;

            try
            {
                int size = Marshal.SizeOf<ShareInfo1>();
                for (int i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<ShareInfo1>(buffer + i * size);
                    bool special = (info.Type & StypeSpecial) != 0;
                    bool disk = (info.Type & StypeBaseMask) == StypeDisktree;
                    if (!special && disk && !string.IsNullOrEmpty(info.Netname))
                        shares.Add(info.Netname);
                }
            }
            finally
            {
                NetApiBufferFree(buffer);
            }

            return shares;
        }, ct);
}
