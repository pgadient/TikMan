namespace TikMan.Core.Discovery;

/// <summary>What TikMan can do with one class of device. Levels rather than plain yes/no, because most of
/// these are "yes, but only with credentials" or "read-only".</summary>
/// <param name="No">TikMan does not do this – a gap on our side, or a deliberate decision. ⚠️ Read it as
/// <b>settled</b>: firmware updates on a third-party switch are a "No" nobody should wait for.</param>
/// <param name="Planned">On the list, not built yet. Kept apart from <see cref="No"/> because the two send
/// opposite signals to someone deciding whether to wait or to work around it – a row of plain "No" on a
/// vendor we are actively building reads as "this will never happen", which is the wrong answer.</param>
/// <param name="NotAvailable">⚠️ The <b>device</b> does not offer it at all, so there is nothing for TikMan
/// to implement. A different statement from <see cref="No"/>, and worth keeping apart: "not supported" reads
/// as a missing feature someone might file a ticket about, while "not available" says the capability does
/// not exist on that hardware. TP-Link's binary full backup is the case in point – its web interface only
/// downloads a .cfg, and that .cfg <i>is</i> the running-config TikMan already fetches over SSH.</param>
public enum SupportLevel { No, NotAvailable, Planned, Partial, Yes }

/// <summary>One row of the vendor support matrix.
///
/// <para>Two kinds of column, deliberately kept apart. <b>Protocols</b> (<see cref="Rest"/>,
/// <see cref="Ssh"/>, <see cref="Snmp"/>) answer "can TikMan get this vendor's data <i>over</i> that
/// protocol" – not "does the device have the port open". A TP-Link switch answers on 443 and tells you
/// nothing without a session, so its REST is <c>No</c> even though the port is there.
/// <b>Features</b> (the rest) answer "what can TikMan then do".</para>
///
/// <para>⚠️ There is no Discovery column: every vendor in this table is discovered, so a column that reads
/// "yes" all the way down carries no information. And the table lists <b>vendors</b> – the rows for "any
/// device with SNMP", Windows PCs and printers were describing discovery <i>methods</i>, which the port
/// scanner already makes obvious.</para></summary>
/// <param name="Vendor">The manufacturer alone – that is what the row is about.</param>
/// <param name="Models">Which of its devices this row covers ("RouterOS v7", "JetStream switches").
/// Kept as its own field rather than folded into the vendor string in brackets, so the view can show the
/// manufacturer as the heading and the models quietly underneath it.</param>
/// <param name="WorkInProgress">This vendor is actively being built out – what the dots show is today's
/// state, not the finished picture. ⚠️ A property of the ROW, not of a cell: "we are working on this
/// vendor" is a different statement from "this particular feature is missing", and folding it into the
/// cells would make a half-finished connector look like a set of deliberate decisions.</param>
public sealed record VendorSupportRow(
    string Vendor,
    string Models,
    // ---- protocols: where the data comes from ----
    SupportLevel Rest,
    SupportLevel Ssh,
    SupportLevel Snmp,
    // ---- features: what TikMan can do with it ----
    SupportLevel Monitoring,
    SupportLevel ConfigBackup,
    SupportLevel FullBackup,
    SupportLevel Updates,
    SupportLevel Topology,
    SupportLevel Logs,
    bool WorkInProgress = false);

/// <summary>The single source of truth for "which vendor supports what", shown to the user in the support
/// matrix. It lives in Core so the GUI, the web UI and the docs can't drift apart.
/// <para>⚠️ Keep this in step with reality: when a vendor gains a capability (a new backup path, monitoring
/// over a new transport), update the row here and nowhere else.</para></summary>
public static class VendorSupport
{
    public static IReadOnlyList<VendorSupportRow> Rows { get; } = new[]
    {
        //                                                     REST   SSH    SNMP  |  Mon    Cfg    Full   Upd    Topo   Logs
        new VendorSupportRow("MikroTik", "RouterOS v7",
            SupportLevel.Yes, SupportLevel.Yes, SupportLevel.Yes,
            SupportLevel.Yes, SupportLevel.Yes, SupportLevel.Yes, SupportLevel.Yes, SupportLevel.Yes,
            SupportLevel.Yes),

        // ⚠️ Updates deliberately No, not "not implemented yet": pushing firmware to a switch is the one
        // action that can leave a device unreachable, so TikMan opens the vendor's download page instead of
        // doing it.
        //
        // ⚠️ Full backup is No because it is genuinely impossible, and this was MEASURED rather than
        // assumed – RouterOS fetches its .backup with an SCP client over the same port 22, so "it is SSH,
        // therefore no file transfer" would have been a bad inference. Against a real TL-SG2008:
        //   SFTP subsystem  -> connection aborted by the server
        //   SCP (scp -f)    -> "Secure copy execution request was rejected"
        //   exec channel    -> connection aborted
        // Its SSH offers an interactive shell and nothing else, and the CLI's own `copy` can only push to a
        // TFTP server. There is no file to pull. (That measurement is also why the command runner here
        // drives a shell instead of exec channels.)
        new VendorSupportRow("TP-Link / Omada", "JetStream managed switches\n(TL-SG2008 verified)",
            // REST: the box has a web interface, but no API behind it – nothing to connect to.
            SupportLevel.NotAvailable, SupportLevel.Yes, SupportLevel.No,
            // Full backup: not a gap, it does not exist. Confirmed from both ends – no transport over SSH
            // (measured below), and the web interface's own "backup" download is a .cfg whose contents are
            // exactly the running-config TikMan already saves. There is no richer artefact to fetch.
            SupportLevel.Yes, SupportLevel.Yes, SupportLevel.NotAvailable, SupportLevel.No, SupportLevel.Yes,
            SupportLevel.Yes),

        // ⚠️ Zyxel is split by OPERATING SYSTEM, not by device role. The OS is what decides the CLI
        // dialect, so it is what decides whether a connector works – a switch and a firewall running the
        // same uOS answer the same commands, while two switches on different Zyxel systems do not. Splitting
        // by role (the earlier "switches" vs "firewalls and APs" rows) described TikMan's implementation
        // history rather than the devices.
        //
        // The SOC-vendor systems on Zyxel's niche hardware are deliberately left out: one connector per
        // chipset vendor is far more work than the devices justify.
        //
        // ⚠️ Presentation only. The runtime gate stays as it is (FleetService.IsZyxelSwitch), because it
        // was introduced to stop ZyNOS commands reaching a device that does not speak them – and until the
        // per-OS connectors exist, that protection is still the thing keeping it honest.
        // ⚠️ REST is NotAvailable rather than No on every Zyxel row: these systems ship a web interface with
        // no API behind it, so there is no endpoint for TikMan to miss. Updates stay a settled No for the
        // same reason as TP-Link – pushing firmware is the one action that can leave a device unreachable.
        // Everything else still missing on a row being built is Planned, not No.
        // ⚠️ No longer work-in-progress: every Yes here is proven against THREE real switches spanning three
        // firmware generations – an XGS1930-52HP on ZyNOS V5.00, a GS1920-48 on V4.50, and a GS2200-48 on
        // V3.80 (2009) – and the connector was hardened for the differences between them (the older sshd's
        // fatal exec channel, its ESC 7 output prefix, its blank port-name column, and a pager that never
        // sits at the end of the line). The V3.80 GS2200 needed two more shapes: `show cpu-utilization` has
        // no headline percentage there but a per-second sec/ticks/util table (the util column is averaged
        // into one figure), and `show logging` carries a day-of-week + year + word-level line rather than the
        // 2-letter class; it also exposes no memory command at all, so monitoring on that generation is
        // CPU-only (the memory column reads 0, not a bug). System information, running-config, the forwarding
        // table AND the log all read over the SSH CLI; the serial, which the GS1920/GS2200 CLI omits, is read
        // from the Zyxel-private OID the web GUI uses.
        // Full backup is NotAvailable, not a gap: measured on all three, these switches offer no binary
        // artefact – the downloadable .cfg IS the running-config the config-backup already fetches, exactly as
        // on TP-Link. Updates stay a settled No – pushing firmware to a switch is the one act that can strand
        // it. SNMP is Yes: TikMan reads what it needs from it – the serial the CLI omits – and it answered on
        // all; everything richer just happens to come over the SSH CLI, which does not make SNMP partial.
        new VendorSupportRow("Zyxel · ZyNOS", "GS / XGS switches\n(GS2200, GS1920, XGS1930 verified)",
            SupportLevel.NotAvailable, SupportLevel.Yes, SupportLevel.Yes,
            SupportLevel.Yes, SupportLevel.Yes, SupportLevel.NotAvailable, SupportLevel.No,
            SupportLevel.Yes, SupportLevel.Yes),

        // ⚠️ PROVEN against a real USG FLEX 500 on ZLD V5.39, and no longer work-in-progress. Identification,
        // monitoring, config backup AND logs all read over the ZLD SSH CLI. ZLD is its own dialect – no "show
        // system-information" (that is ZyNOS); it has a two-image "show version" table (the Running image is
        // the live model + firmware), "show cpu status" / "show mem status" / "show system uptime" / "show
        // serial-number", "show running-config" and "show logging entries". Full backup is NotAvailable: the
        // config IS the whole backup, no binary artefact (measured – only a .conf).
        // ⚠️ Topology is Yes, and every mechanism behind it was MEASURED, not assumed. The firewall is PLACED
        // three ways, most-trusted first. (1) `show zon lldp neighbors` (when the user has enabled `zon lldp
        // server`): a DIRECT link, naming the placed neighbour and the exact far-end port ("combo4"). It also
        // gives the firewall's own uplink port, which — with `show port status` (link-up ports) and `show
        // port-grouping` (which physical ports a group spans) — narrows a client's label from the whole group
        // "P2-P8 (lan1)" down to the real port "P6 (lan1)". (2) `show mac` reveals its own MAC block, matched
        // against the switches' forwarding tables. (3) The shared-witness rule for a firewall no switch has
        // ever seen (a workbench switch talks only to the hosts behind it, all switched locally): its ARP
        // names its hosts, the switches prove where those attach, and a unanimous attach point places it
        // between them (its hosts re-home beneath it). What deliberately does NOT feed the map: the ARP as a
        // forwarding table – it is L3 adjacency, and fed in as L2 evidence it pulled every ARPed host under
        // the firewall. The ZLD CLI has no switch-MAC-table command at all (the full `show ?` list was
        // walked), so the firewall contributes no forwarding table of its own – placed, but not a source.
        // LLDP is read only when the user turns it on; TikMan never flips that config itself. Without it the
        // firewall still places (mechanisms 2+3) and the client label falls back to the active-port set, then
        // the whole group. Updates stay a settled No, like the switches. SNMP is not used – everything comes
        // over the CLI. These boxes miscompute encrypt-then-MAC, so SshCompat offers only the plain HMACs, or
        // the session dies on the first encrypted packet. Sits under ZyNOS because both are managed Zyxel SSH.
        new VendorSupportRow("Zyxel · ZLD", "ZyWALL / USG / USG FLEX firewalls\n(USG FLEX 500 verified)",
            SupportLevel.NotAvailable, SupportLevel.Yes, SupportLevel.No,
            SupportLevel.Yes, SupportLevel.Yes, SupportLevel.NotAvailable, SupportLevel.No,
            SupportLevel.Yes, SupportLevel.Yes),

        new VendorSupportRow("Zyxel · uOS", "Nebula devices\n(USG FLEX H, current switches and APs)",
            // Identified over ZON and the web fingerprint; the CLI connector is what is being built.
            SupportLevel.NotAvailable, SupportLevel.Partial, SupportLevel.Partial,
            SupportLevel.Planned, SupportLevel.Planned, SupportLevel.Planned, SupportLevel.No,
            SupportLevel.Partial, SupportLevel.Planned, WorkInProgress: true),

        // Nothing implemented yet – listed so the table says "planned and being worked on" rather than
        // leaving the vendor out and implying it was never considered. Hence Planned throughout: a row of
        // grey "No" would have said the opposite of what the WIP badge beside it says.
        new VendorSupportRow("Ubiquiti", "UniFi devices",
            SupportLevel.Planned, SupportLevel.Planned, SupportLevel.Planned,
            SupportLevel.Planned, SupportLevel.Planned, SupportLevel.Planned, SupportLevel.No,
            SupportLevel.Planned, SupportLevel.Planned, WorkInProgress: true),
    };

    /// <summary>True when TikMan can only reach this vendor's devices over SSH, so a credentials dialog
    /// should not offer HTTP/HTTPS at all.
    ///
    /// <para>⚠️ Measured, not assumed. A TP-Link JetStream switch answers on 80 and 443, which makes the web
    /// options <i>look</i> reasonable – but both serve nothing except a JavaScript shell ("Loading…",
    /// <c>Server: Web Switch</c>) and hand out no device facts at all without a session, and TikMan has no
    /// connector for that login. Everything it reads from these switches – model, hardware revision,
    /// firmware, serial, uptime, forwarding table, LLDP neighbours, CPU/RAM – comes from the SSH CLI.
    /// Offering a transport that cannot work only invites a login that silently never succeeds.</para>
    ///
    /// <para>Zyxel switches are the same story (their config backup and monitoring are SSH-only too).</para></summary>
    public static bool IsSshOnly(string vendor)
    {
        var v = (vendor ?? "").Trim();
        if (v.Length == 0) return false;
        return v.Contains("TP-Link", StringComparison.OrdinalIgnoreCase)
            || v.Contains("TPLink", StringComparison.OrdinalIgnoreCase)
            || v.Contains("Omada", StringComparison.OrdinalIgnoreCase);
    }
}
