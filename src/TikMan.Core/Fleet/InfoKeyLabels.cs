using System.Collections.Generic;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.Core.Fleet;

/// <summary>Turns the internal ExtraInfo dictionary keys – which are German, because they double as lookup
/// keys the classifier reads (<c>ExtraInfo["Modell"]</c>, <c>["Hersteller (Web)"]</c> …) – into a localised
/// label for display in the details pane. The keys themselves can't just be renamed without touching every
/// place that reads them, so the translation happens once, on the way to the UI.
/// <para>Keys that read the same in every language (Firmware, OS, SNMP, System, Version) aren't in the map
/// and pass through unchanged.</para></summary>
public static class InfoKeyLabels
{
    private static readonly Dictionary<string, string> ToLocKey = new()
    {
        ["Hersteller"] = "Ik_Manufacturer",
        ["Hersteller (Web)"] = "Ik_ManufacturerWeb",
        ["Modell"] = "Ik_Model",
        ["Bauform"] = "Ik_FormFactor",
        ["Produkt"] = "Ik_Product",
        ["Seriennummer"] = "Ik_Serial",
        ["Hardware-Version"] = "Ik_HwVersion",
        ["Web-Titel"] = "Ik_WebTitle",
        ["Webserver"] = "Ik_WebServer",
        ["mDNS-Modell"] = "Ik_MdnsModel",
        ["Druckerfreigabe"] = "Ik_PrinterShare",
        ["Weitere MAC"] = "Ik_ExtraMac",
        ["MAC-Bereich"] = "Ik_MacRange",
        ["MAC-Zuordnung"] = "Ik_MacAssign",
        ["Adoptiert"] = "Ik_Adopted",
        ["Inform-URL"] = "Ik_InformUrl",
        ["Herstelldatum"] = "Ik_MfgDate",
        ["Druckserver"] = "Ik_PrintServer",
        ["Firmware-Dateien"] = "Ik_FirmwareFiles",
    };

    /// <summary>The localised label for an ExtraInfo key, or the key itself when it needs no translation.</summary>
    public static string Localize(string key) => ToLocKey.TryGetValue(key, out var locKey) ? T(locKey) : key;
}
