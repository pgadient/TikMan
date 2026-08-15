using System.Text.RegularExpressions;
using TikMan.Core.Api;

namespace TikMan.Core.Discovery;

/// <summary>Pulls a concise model plus structured detail fields out of an SNMP <c>sysDescr</c>, for vendors
/// that pack a whole nameplate into that one string – so the model column stays short and the nameplate detail
/// (firmware, serial, hardware revision, manufacture date, print-server type …) moves into the details pane.
///
/// <para>Handled today:</para>
/// <list type="bullet">
/// <item><b>APC / Schneider</b> UPS management cards: <c>APC Web/SNMP Management Card (… MN:AP9641 HR:5 SN: ZA…
/// MD:07/30/2022) …</c> → model <c>AP9641</c>, the rest to details. APC has no web page, so this MN is the
/// model.</item>
/// <item><b>Brother</b> print servers: <c>NC-8600h, Firmware Ver.S ,MID 8xx-xxx,FID 2</c> (MID anonymised) → this is the network
/// card, NOT the printer. The real model ("Brother MFC-L9550CDW") comes from the web title, so
/// <see cref="DescrInfo.PreferWebTitle"/> is set and the NC-nameplate is demoted to a detail.</item>
/// </list>
///
/// <para>⚠️ Pure and defensive so it can be pinned. An unrecognised sysDescr falls straight through — the model
/// is the raw string and there are no extras, i.e. current behaviour is unchanged. The sysDescr is UNTRUSTED
/// device text; every field is read with a bounded regex and never interpreted as anything but a value.</para></summary>
public static class SnmpDescr
{
    /// <param name="Model">The concise model, else the raw sysDescr.</param>
    /// <param name="Serial">Serial when the description carried one, else "".</param>
    /// <param name="Vendor">A vendor hint so classification still fires once the model is shortened, else "".</param>
    /// <param name="PreferWebTitle">True when the parsed model is a SECONDARY nameplate (a Brother print
    /// server) and a web title, if present, is the better model. The caller then demotes
    /// <see cref="Model"/> to a detail under <see cref="ModelDetailKey"/> instead of the model column.</param>
    /// <param name="ModelDetailKey">The ExtraInfo key to file the model under when it is demoted (e.g.
    /// "Druckserver"); "" when not applicable.</param>
    /// <param name="Extras">Named detail fields, German keys so they slot into the existing ExtraInfo /
    /// InfoKeyLabels scheme. Empty for an unrecognised description.</param>
    public readonly record struct DescrInfo(string Model, string Serial, string Vendor, bool PreferWebTitle,
        string ModelDetailKey, IReadOnlyList<(string Key, string Value)> Extras);

    private static readonly IReadOnlyList<(string, string)> None = Array.Empty<(string, string)>();

    public static DescrInfo Parse(string? sysDescr)
    {
        var s = (sysDescr ?? "").Trim();
        if (s.Length == 0) return new DescrInfo("", "", "", false, "", None);

        // APC / Schneider Network Management Card: an "APC …" description with the MN:/HR:/SN: nameplate block.
        if (s.Contains("APC", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(s, @"\bMN:", RegexOptions.IgnoreCase))
            return ParseApc(s);

        // Brother print/scan server: "NC-<n>…" with the Firmware/MID/FID nameplate. Not the printer itself.
        if (Regex.IsMatch(s, @"\bNC-\w", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(s, @"\b(MID|FID|Firmware)\b", RegexOptions.IgnoreCase))
            return ParseBrother(s);

        return new DescrInfo(s, "", "", false, "", None);   // unchanged fallback: raw sysDescr as the model
    }

    private static DescrInfo ParseApc(string s)
    {
        // Each token is "KEY:value"; the value runs to the next space or the closing paren. SN carries a stray
        // space ("SN: ZA…"), which \s* after the colon absorbs. Anchored on a word boundary so "MN" doesn't
        // match inside another token.
        string Field(string key)
        {
            var m = Regex.Match(s, @"\b" + key + @":\s*([^\s)]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        var model = Field("MN");     // model number  → column
        var serial = Field("SN");    // serial number → column
        var appFw = Field("AF1");    // application firmware version (the one an operator cares about)
        var aos = Field("PF");       // APC OS (AOS) firmware version
        var boot = Field("MB");      // bootmonitor / management board firmware
        var hwRev = Field("HR");     // hardware revision
        var mfgDate = Field("MD");   // manufacture date
        var aosFile = Field("PN");   // AOS firmware file name
        var appFile = Field("AN1");  // application firmware file name

        // Nothing from the nameplate is dropped – every field goes to the details. The version fields carry
        // their source code in parens ("v2.5.5.1 (AF1)") so the two firmwares and the bootmonitor are told
        // apart; the plain fields (hardware revision, manufacture date) need no code.
        // ⚠️ Strip the leading "v" APC writes on its versions ("v2.5.5.1" → "2.5.5.1") – Schneider's own
        // download listing shows the number without it, so the installed and latest columns read consistently.
        var extras = new List<(string, string)>();
        if (appFw.Length > 0) extras.Add(("Firmware", $"{StripV(appFw)} (AF1)"));
        if (aos.Length > 0) extras.Add(("AOS-Firmware", $"{StripV(aos)} (PF)"));
        if (boot.Length > 0) extras.Add(("Bootmonitor", $"{StripV(boot)} (MB)"));
        if (hwRev.Length > 0) extras.Add(("Hardware-Version", hwRev));
        if (mfgDate.Length > 0) extras.Add(("Herstelldatum", mfgDate));
        var files = new List<string>();
        if (aosFile.Length > 0) files.Add($"{aosFile} (PN)");
        if (appFile.Length > 0) files.Add($"{appFile} (AN1)");
        if (files.Count > 0) extras.Add(("Firmware-Dateien", string.Join(", ", files)));

        // The application firmware file base ("apc_hw21_su") drives the latest-firmware lookup: it names the
        // exact product+application, so it disambiguates an AP9641 in a Smart-UPS from the same card in a
        // Symmetra (the model number alone can't). Hidden key – for the lookup, not the details pane.
        var appBase = appFile.Length > 0 ? ApcFirmware.FileBase(appFile) : "";
        if (appBase.Length > 0) extras.Add(("APC-App", appBase));

        // MN missing (an APC variant we don't know) → keep the raw string rather than a blank model.
        return new DescrInfo(model.Length > 0 ? model : s, serial, "American Power Conversion", false, "", extras);
    }

    // "v2.5.5.1" → "2.5.5.1"; only a leading v/V directly before a digit, so a real value is left alone.
    private static string StripV(string s) =>
        s.Length > 1 && (s[0] is 'v' or 'V') && char.IsDigit(s[1]) ? s[1..] : s;

    private static DescrInfo ParseBrother(string s)
    {
        // "NC-8600h, Firmware Ver.S  ,MID 8xx-xxx,FID 2" – comma-separated; the first token is the print server.
        var server = s.Split(',')[0].Trim();
        string One(string pattern)
        {
            var m = Regex.Match(s, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        var fw = One(@"Firmware\s+Ver\.?\s*([^\s,]+)");   // "Ver.S" → "S"
        var mid = One(@"\bMID\s+([^\s,]+)");
        var fid = One(@"\bFID\s+([^\s,]+)");

        // Fold the firmware into the print-server label so it reads as one nameplate ("NC-8600h (Ver. S)").
        var serverLabel = fw.Length > 0 ? $"{server} (Ver. {fw})" : server;

        var extras = new List<(string, string)>();
        if (mid.Length > 0) extras.Add(("MID", mid));
        if (fid.Length > 0) extras.Add(("FID", fid));

        // PreferWebTitle: the web title ("Brother MFC-L9550CDW") is the real model; this NC- string is the
        // network card, so it belongs in a "Druckserver" detail whenever a web title exists.
        return new DescrInfo(serverLabel, "", "Brother", true, "Druckserver", extras);
    }
}
