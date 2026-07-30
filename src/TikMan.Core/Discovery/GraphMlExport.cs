using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace TikMan.Core.Discovery;

/// <summary>Writes a topology map as GraphML – the one open XML graph format with real tool support
/// (yEd, Gephi, Cytoscape, NetworkX, igraph all read it).
///
/// <para><b>Why GraphML and not something network-specific:</b> there is no widely-supported standard for
/// network topology as such. The candidates are either vendor formats (draw.io's mxGraph XML), niche
/// (GEXF), or academic with essentially no tooling (NDL, CIM). GraphML is a graph format rather than a
/// network one, but it carries arbitrary typed attributes – so the device type, vendor, addresses and,
/// importantly, whether a link was <i>measured or asserted</i> all survive the export instead of being
/// flattened into anonymous boxes and lines.</para>
///
/// <para>⚠️ Unlike the diagnostic log, this deliberately contains the real addresses and MACs. It is an
/// export the user asked for, describing their own network, to open in their own tools – redacting it
/// would make it useless. The log is the opposite case: shared with strangers, so scrubbed.</para></summary>
public static class GraphMlExport
{
    /// <summary>The yFiles graphics namespace. GraphML itself has no notion of position, size, colour or
    /// caption; this extension is how yEd (and anything that reads its files) is told how to draw.</summary>
    private const string Y = "http://www.yworks.com/xml/graphml";


    /// <summary>Renders a layout, plus whatever the user added by hand, as a GraphML document.</summary>
    /// <param name="view">"logical" or "physical" – recorded on the graph so the file says what it is.</param>
    /// <param name="manualNodeKeys">Keys of hand-added nodes, so they can be marked as asserted.</param>
    /// <param name="manualEdges">Hand-drawn connections, appended and marked.</param>
    public static string Build(TopoLayout layout, string view,
        IReadOnlyCollection<string>? manualNodeKeys = null,
        IReadOnlyCollection<(string From, string To)>? manualEdges = null)
    {
        var manual = manualNodeKeys ?? Array.Empty<string>();
        var extra = manualEdges ?? Array.Empty<(string, string)>();

        // ⚠️ Utf8StringWriter, not a plain StringBuilder: XmlWriter takes the declared encoding from
        // its writer, and the default reports UTF-16 – producing a file whose declaration contradicts its
        // own bytes.
        using var sw = new Utf8StringWriter();
        using var w = XmlWriter.Create(sw, new XmlWriterSettings { Indent = true, IndentChars = "  " });

        w.WriteStartDocument();
        w.WriteStartElement("graphml", "http://graphml.graphdrawing.org/xmlns");
        w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
        // ⚠️ The yFiles namespace is what makes this file *drawable*. Plain GraphML describes structure
        // only – position, size, colour and even the visible caption are outside the standard, so a reader
        // that finds only <data> keys has nothing to render and falls back to identical default boxes
        // stacked on top of each other. That is not a malformed file, it is a file with no graphics in it.
        w.WriteAttributeString("xmlns", "y", null, Y);
        w.WriteAttributeString("xsi", "schemaLocation", null,
            "http://graphml.graphdrawing.org/xmlns http://graphml.graphdrawing.org/xmlns/1.0/graphml.xsd");

        // Attribute declarations. GraphML requires every <data> to reference a declared key, which is what
        // lets a reader show these as real columns rather than opaque strings.
        DeclareKey(w, "d_label", "node", "label", "string");
        DeclareKey(w, "d_detail", "node", "detail", "string");
        DeclareKey(w, "d_vendor", "node", "vendor", "string");
        DeclareKey(w, "d_model", "node", "model", "string");
        // ⚠️ This key was declared but never written – so every reader saw a "kind" column that was empty
        // on every row. It carries the device category now.
        DeclareKey(w, "d_kind", "node", "kind", "string");
        DeclareKey(w, "d_ip", "node", "ip", "string");
        DeclareKey(w, "d_mac", "node", "mac", "string");
        DeclareKey(w, "d_source", "node", "source", "string");
        DeclareKey(w, "d_x", "node", "x", "double");
        DeclareKey(w, "d_y", "node", "y", "double");
        DeclareKey(w, "e_source", "edge", "source_kind", "string");

        // The graphics keys. These carry no attr.name/attr.type – they are declared by yfiles.type, and a
        // reader that does not know yFiles simply skips them, so the file stays valid everywhere.
        DeclareGraphicsKey(w, "d_graphics", "node", "nodegraphics");
        DeclareGraphicsKey(w, "e_graphics", "edge", "edgegraphics");

        w.WriteStartElement("graph");
        w.WriteAttributeString("id", "tikman-" + view);
        // Undirected: a cable has no direction, and marking it directed would invent one.
        w.WriteAttributeString("edgedefault", "undirected");

        foreach (var n in layout.Nodes)
        {
            var isManual = manual.Contains(n.Key);
            w.WriteStartElement("node");
            w.WriteAttributeString("id", n.Key);
            Data(w, "d_label", n.Title);
            Data(w, "d_detail", n.Detail);
            // ⚠️ Their own keys, not folded into the label: GraphML is the export people load into yEd or
            // NetworkX to analyse, and "group every switch by vendor" only works if the vendor is a field
            // rather than a word inside a caption.
            // Two fields rather than one for the same reason: "group everything by maker" and "find all the
            // SG2008s" are only queries if the model is not glued to the vendor inside one string.
            Data(w, "d_vendor", n.Vendor);
            Data(w, "d_model", n.Model);
            Data(w, "d_kind", n.Kind);
            Data(w, "d_mac", n.Mac);
            // DeviceId is the fleet's id, which is the MAC when there is one and the host otherwise –
            // useful as the address when it looks like one.
            Data(w, "d_ip", LooksLikeAddress(n.DeviceId) ? n.DeviceId : "");
            // ⚠️ The distinction that matters: measured nodes were found, manual ones were asserted.
            Data(w, "d_source", isManual ? "manual" : "discovered");
            Data(w, "d_x", n.X.ToString("0.##", CultureInfo.InvariantCulture));
            Data(w, "d_y", n.Y.ToString("0.##", CultureInfo.InvariantCulture));
            WriteNodeGraphics(w, n, isManual);
            w.WriteEndElement();
        }

        var edgeId = 0;
        foreach (var e in layout.Edges)
            WriteEdge(w, ref edgeId, e.From, e.To, "discovered");
        foreach (var (from, to) in extra)
            WriteEdge(w, ref edgeId, from, to, "manual");

        w.WriteEndElement(); // graph
        w.WriteEndElement(); // graphml
        w.WriteEndDocument();
        w.Flush();
        return sw.ToString();
    }

    private static void WriteEdge(XmlWriter w, ref int id, string from, string to, string kind)
    {
        w.WriteStartElement("edge");
        w.WriteAttributeString("id", "e" + id++);
        w.WriteAttributeString("source", from);
        w.WriteAttributeString("target", to);
        Data(w, "e_source", kind);

        // Drawing instructions for the same line: straight, and with no arrowhead – the map is undirected.
        w.WriteStartElement("data");
        w.WriteAttributeString("key", "e_graphics");
        w.WriteStartElement("PolyLineEdge", Y);
        w.WriteStartElement("LineStyle", Y);
        w.WriteAttributeString("color", kind == "manual" ? "#78909C" : "#C4CCD2");
        w.WriteAttributeString("type", kind == "manual" ? "dashed" : "line");
        w.WriteAttributeString("width", "1.5");
        w.WriteEndElement();
        w.WriteStartElement("Arrows", Y);
        w.WriteAttributeString("source", "none");
        w.WriteAttributeString("target", "none");
        w.WriteEndElement();
        w.WriteEndElement(); // PolyLineEdge
        w.WriteEndElement(); // data

        w.WriteEndElement();
    }

    /// <summary>The node as an actual drawn shape: where it sits, how big it is, its colours and its
    /// caption. Without this a reader has no rendering information at all and every node comes out as an
    /// identical default box at the origin.</summary>
    private static void WriteNodeGraphics(XmlWriter w, TopoBox n, bool isManual)
    {
        w.WriteStartElement("data");
        w.WriteAttributeString("key", "d_graphics");
        w.WriteStartElement("ShapeNode", Y);

        w.WriteStartElement("Geometry", Y);
        w.WriteAttributeString("x", Num(n.X));
        w.WriteAttributeString("y", Num(n.Y));
        w.WriteAttributeString("width", Num(n.W));
        w.WriteAttributeString("height", Num(n.H));
        w.WriteEndElement();

        w.WriteStartElement("Fill", Y);
        w.WriteAttributeString("color", n.Fill);
        w.WriteAttributeString("transparent", "false");
        w.WriteEndElement();

        w.WriteStartElement("BorderStyle", Y);
        w.WriteAttributeString("color", n.Line);
        // A hand-added node is drawn dashed, the same signal the app and the draw.io export use.
        w.WriteAttributeString("type", isManual ? "dashed" : "line");
        w.WriteAttributeString("width", isManual ? "2.0" : "1.0");
        w.WriteEndElement();

        // ⚠️ ONE label carrying every line (see NodeCaption). yFiles positions each NodeLabel independently,
        // so separate labels for the separate lines land on top of each other in the middle of the box.
        w.WriteStartElement("NodeLabel", Y);
        w.WriteAttributeString("alignment", "left");
        w.WriteAttributeString("autoSizePolicy", "content");
        w.WriteAttributeString("fontFamily", "Dialog");
        // ⚠️ 11pt and anchored TOP-LEFT, not 12pt centred. The label grew from three lines to six, and a
        // centred label that outgrows its node spills past BOTH edges – which is what made neighbouring
        // boxes look broken and overlapping. Anchored at the top-left it starts where every other renderer
        // starts, grows downward only, and with the lines clipped it now fits inside the box.
        w.WriteAttributeString("fontSize", "11");
        w.WriteAttributeString("textColor", n.Text);
        w.WriteAttributeString("modelName", "internal");
        w.WriteAttributeString("modelPosition", "tl");
        w.WriteAttributeString("visible", "true");
        w.WriteString(NodeCaption(n));
        w.WriteEndElement();

        w.WriteStartElement("Shape", Y);
        w.WriteAttributeString("type", "roundrectangle");
        w.WriteEndElement();

        w.WriteEndElement(); // ShapeNode
        w.WriteEndElement(); // data
    }

    /// <summary>What the box shows in yEd: category, name, vendor, model, address, MAC – the same six lines
    /// in the same order as the app's own map and the PDF/PNG/draw.io exports.
    ///
    /// <para>⚠️ This used to be title + detail + MAC only. Vendor, model and category were written as
    /// <c>&lt;data&gt;</c> attributes and nowhere else, so they were present in the file and invisible in the
    /// editor – opening the export showed less than every other export of the same map.</para>
    ///
    /// <para>⚠️ Written as HTML so the name can be bold like everywhere else. yEd renders a label as HTML
    /// when its text starts with <c>&lt;html&gt;</c>, and a single label is still required: yFiles positions
    /// every NodeLabel independently, so separate labels for the separate lines stack on top of each other
    /// in the middle of the box. Device text is HTML-escaped first – <see cref="XmlWriter"/> escapes the
    /// whole string once more on the way into the attribute, and yEd unescapes exactly that one layer, so
    /// what reaches the HTML parser is what this method built.</para>
    ///
    /// <para>Tools that read GraphML as data (NetworkX, Gephi) never look at the yFiles label – they read
    /// the <c>&lt;data&gt;</c> keys, which carry every field separately and unformatted.</para></summary>
    private static string NodeCaption(TopoBox n)
    {
        // ⚠️ Colours, not opacity. yEd's HTML label subset understands <font color=…> and ignores CSS
        // opacity, so the shading is pre-blended toward the box fill – the same pixel, expressed as a plain
        // colour. TopoColours holds the amounts so this cannot drift from the canvas and the other exports.
        // ⚠️ Every line is clipped (see TopoLabel). yEd sizes the label to its content and lets it run past
        // the node, so one device that reports a paragraph as its model drew a line across the diagram and
        // overlapped everything beside it. The full value stays in the <data> attributes below.
        string Line(string text, int max, double fade) =>
            $"<font color=\"{TopoColours.Fade(n.Text, n.Fill, fade)}\">{Html(TopoLabel.Clip(text, max))}</font>";

        var sb = new StringBuilder("<html>");
        if (!string.IsNullOrEmpty(n.Kind))
            sb.Append(Line(n.Kind, TopoLabel.KindMax, TopoColours.KindFade)).Append("<br>");
        sb.Append("<b>").Append(Html(TopoLabel.Clip(n.Title, TopoLabel.TitleMax))).Append("</b>");
        if (!string.IsNullOrEmpty(n.Vendor))
            sb.Append("<br>").Append(Line(n.Vendor, TopoLabel.HardwareMax, TopoColours.HardwareFade));
        if (!string.IsNullOrEmpty(n.Model))
            sb.Append("<br>").Append(Line(n.Model, TopoLabel.HardwareMax, TopoColours.HardwareFade));
        if (!string.IsNullOrEmpty(n.Detail))
            sb.Append("<br>").Append(Line(n.Detail, TopoLabel.DetailMax, TopoColours.DetailFade));
        if (!string.IsNullOrEmpty(n.Mac))
            sb.Append("<br>").Append(Line(n.Mac, TopoLabel.MacMax, TopoColours.MacFade));
        return sb.Append("</html>").ToString();
    }

    /// <summary>Escapes the three characters that would otherwise be read as markup inside the label.</summary>
    private static string Html(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static void DeclareGraphicsKey(XmlWriter w, string id, string domain, string type)
    {
        w.WriteStartElement("key");
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("for", domain);
        w.WriteAttributeString("yfiles.type", type);
        w.WriteEndElement();
    }

    private static void DeclareKey(XmlWriter w, string id, string domain, string name, string type)
    {
        w.WriteStartElement("key");
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("for", domain);
        w.WriteAttributeString("attr.name", name);
        w.WriteAttributeString("attr.type", type);
        w.WriteEndElement();
    }

    /// <summary>Writes one attribute, skipping empties – an absent value is better than an empty one, and
    /// keeps the file readable.</summary>
    private static void Data(XmlWriter w, string key, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        w.WriteStartElement("data");
        w.WriteAttributeString("key", key);
        w.WriteString(value);   // XmlWriter escapes &, <, > and quotes for us
        w.WriteEndElement();
    }

    private static bool LooksLikeAddress(string s) =>
        s.Length > 0 && (s.Contains('.') || s.Contains(':')) && !s.Contains(' ');
}
