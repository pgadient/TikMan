using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;

namespace TikMan.Core.Discovery;

/// <summary>Offline manufacturer lookup from a MAC address (IEEE OUI = first 3 bytes).
///
/// Ships with the MikroTik OUIs built in, so MikroTik devices always resolve. For full
/// coverage of every vendor, drop the public IEEE list as a plain-text <c>oui.txt</c>
/// (the "oui.txt" format with "(hex)" lines from standards-oui.ieee.org) into
/// <c>%AppData%\TikMan\</c> or next to the executable — it is merged in on first use.
/// The list is public and free to redistribute.</summary>
public static class OuiLookup
{
    // Built-in: MikroTik / SIA Mikrotikls registered OUIs (high confidence).
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
    {
        ["000C42"] = "MikroTik", ["085531"] = "MikroTik", ["18FD74"] = "MikroTik",
        ["2CC81B"] = "MikroTik", ["488F5A"] = "MikroTik", ["4C5E0C"] = "MikroTik",
        ["64D154"] = "MikroTik", ["6C3B6B"] = "MikroTik", ["744D28"] = "MikroTik",
        ["789A18"] = "MikroTik", ["B869F4"] = "MikroTik", ["C4AD34"] = "MikroTik",
        ["CC2DE0"] = "MikroTik", ["D4CA6D"] = "MikroTik", ["DC2C6E"] = "MikroTik",
        ["E48D8C"] = "MikroTik", ["44D9E7"] = "MikroTik",
    };

    private static readonly ConcurrentDictionary<string, string> Db = new(BuiltIn);
    private static int _fileLoaded; // 0 = not yet, 1 = done (loaded once, thread-safe)

    /// <summary>Returns the manufacturer for a MAC address, or "" if unknown.</summary>
    public static string Lookup(string mac)
    {
        if (Interlocked.CompareExchange(ref _fileLoaded, 1, 0) == 0)
        {
            TryLoadEmbedded();                              // full IEEE list bundled into the assembly (gzip)
            foreach (var kv in BuiltIn) Db[kv.Key] = kv.Value; // friendly names (e.g. "MikroTik") win over raw IEEE
            TryLoadFile();                                  // optional newer oui.txt provided by the user wins last
        }

        var prefix = NormalizePrefix(mac);
        return prefix.Length == 6 && Db.TryGetValue(prefix, out var vendor) ? vendor : "";
    }

    /// <summary>Loads the full IEEE OUI list embedded as a gzip resource (oui.txt.gz).</summary>
    private static void TryLoadEmbedded()
    {
        try
        {
            using var raw = Assembly.GetExecutingAssembly().GetManifestResourceStream("oui.txt.gz");
            if (raw is null) return;
            using var gz = new GZipStream(raw, CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                ParseLine(line);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // fall back to the built-in MikroTik set
        }
    }

    private static string NormalizePrefix(string mac)
    {
        Span<char> buf = stackalloc char[6];
        int n = 0;
        foreach (var c in mac)
        {
            if (!Uri.IsHexDigit(c)) continue;
            buf[n++] = char.ToUpperInvariant(c);
            if (n == 6) break;
        }
        return n == 6 ? new string(buf) : "";
    }

    /// <summary>Candidate locations for an optional full IEEE oui.txt.</summary>
    private static IEnumerable<string> FileCandidates()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TikMan", "oui.txt");
        yield return Path.Combine(AppContext.BaseDirectory, "oui.txt");
    }

    private static void TryLoadFile()
    {
        foreach (var path in FileCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                    ParseLine(line);
                return; // first existing file wins
            }
            catch (IOException) { /* unreadable file – keep the built-in set */ }
        }
    }

    /// <summary>Parses an IEEE oui.txt "(hex)" line, e.g.
    /// "  4C-5E-0C   (hex)\t\tSIA Mikrotikls".</summary>
    private static void ParseLine(string line)
    {
        int marker = line.IndexOf("(hex)", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return;

        var prefix = NormalizePrefix(line[..marker]);
        if (prefix.Length != 6) return;

        var vendor = line[(marker + 5)..].Trim();
        if (vendor.Length > 0)
            Db[prefix] = vendor; // file entries extend/override the built-in set
    }
}
