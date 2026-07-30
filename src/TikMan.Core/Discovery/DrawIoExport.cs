using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace TikMan.Core.Discovery;

/// <summary>Writes a topology map as a draw.io (diagrams.net) file.
///
/// <para><b>Why this as well as GraphML:</b> they serve different purposes. GraphML is for tools that
/// <i>analyse</i> a graph – Gephi, Cytoscape, NetworkX. draw.io is where people <i>keep</i> a network
/// diagram: open the file, drag things around, add the bits TikMan cannot see, hand it to a colleague.
/// Exporting only the analysis format would miss what most people actually do with a network map.</para>
///
/// <para>Node facts ride in an <c>&lt;object&gt;</c> wrapper rather than being baked into the label, so
/// they show up in draw.io's "Edit Data" dialog as real attributes and survive editing. The
/// discovered/asserted distinction is carried both as an attribute and visually (dashed), same as on
/// screen – a diagram that presents a guess as a measurement is worse than no diagram.</para></summary>
public static class DrawIoExport
{
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
        w.WriteStartElement("mxfile");
        w.WriteAttributeString("host", "TikMan");

        w.WriteStartElement("diagram");
        w.WriteAttributeString("id", "tikman-" + view);
        w.WriteAttributeString("name", view == "physical" ? "Physical topology" : "Logical topology");

        w.WriteStartElement("mxGraphModel");
        w.WriteAttributeString("grid", "1");
        w.WriteAttributeString("gridSize", "10");
        w.WriteAttributeString("page", "1");
        w.WriteAttributeString("math", "0");
        w.WriteAttributeString("shadow", "0");

        w.WriteStartElement("root");

        // ⚠️ Cells "0" and "1" are mandatory: 0 is the model root and 1 is the default layer that every
        // other cell parents to. draw.io opens an empty canvas without them.
        Cell(w, "0", null);
        Cell(w, "1", "0");

        // ⚠️ Edges FIRST, boxes after. mxGraph paints its cells in document order, so whatever is written
        // last ends up on top – and with the boxes written first every connection was drawn straight across
        // them, striping the labels. This is the same thing draw.io's own "send to back" does: it moves the
        // cell earlier in the list. The node ids therefore have to be known before the edges are written,
        // which is what this set is for (an edge to a node that is not in the layout is skipped).
        var known = layout.Nodes.Select(n => Id(n.Key)).ToHashSet();

        var edgeId = 0;
        foreach (var e in layout.Edges)
            Edge(w, ref edgeId, Id(e.From), Id(e.To), known, manualLink: false);
        foreach (var (from, to) in extra)
            Edge(w, ref edgeId, Id(from), Id(to), known, manualLink: true);

        foreach (var n in layout.Nodes)
        {
            var id = Id(n.Key);
            var isManual = manual.Contains(n.Key);

            // <object> rather than a bare <mxCell>: the attributes become editable data in draw.io.
            w.WriteStartElement("object");
            w.WriteAttributeString("id", id);
            // ⚠️ The label carries everything the node shows on screen, not just the title. Previously the
            // detail line and the MAC were attributes only – correct data, but the exported map rendered as
            // bare captions and looked nothing like the view it came from. mxGraph renders HTML in a label
            // when the style says html=1, so the same two-line card is reproducible with <br>.
            w.WriteAttributeString("label", NodeLabel(n));
            // Still emitted as separate attributes as well: that is what makes them editable fields in
            // draw.io and keeps them machine-readable for anything else reading the file.
            if (!string.IsNullOrEmpty(n.Detail)) w.WriteAttributeString("detail", n.Detail);
            if (!string.IsNullOrEmpty(n.Mac)) w.WriteAttributeString("mac", n.Mac);
            if (LooksLikeAddress(n.DeviceId)) w.WriteAttributeString("address", n.DeviceId);
            w.WriteAttributeString("source_kind", isManual ? "manual" : "discovered");

            w.WriteStartElement("mxCell");
            // Dashed outline for a hand-added node, matching what the app shows.
            var style = $"rounded=1;whiteSpace=wrap;html=1;fillColor={n.Fill};strokeColor={n.Line};" +
                        $"fontColor={n.Text};align=left;verticalAlign=top;spacingLeft=8;spacingTop=4;" +
                        $"arcSize=14;" + (isManual ? "dashed=1;strokeWidth=2;" : "");
            w.WriteAttributeString("style", style);
            w.WriteAttributeString("vertex", "1");
            w.WriteAttributeString("parent", "1");
            Geometry(w, n.X, n.Y, n.W, n.H);
            w.WriteEndElement(); // mxCell

            w.WriteEndElement(); // object
        }

        w.WriteEndElement(); // root
        w.WriteEndElement(); // mxGraphModel
        w.WriteEndElement(); // diagram
        w.WriteEndElement(); // mxfile
        w.WriteEndDocument();
        w.Flush();
        return sw.ToString();
    }

    /// <summary>The node's on-screen card as an HTML label: bold title, then the smaller detail line, then
    /// the MAC – the same order and emphasis the app draws.
    ///
    /// <para>⚠️ Written as plain text containing HTML tags. <see cref="XmlWriter"/> escapes it into the
    /// attribute, draw.io unescapes it when parsing, and <c>html=1</c> in the style makes it render. Do not
    /// pre-escape it here or the tags show up literally in the box.</para></summary>
    private static string NodeLabel(TopoBox n)
    {
        // ⚠️ Every line is clipped (see TopoLabel). draw.io lays the label out itself and lets it run past
        // the shape, so a device that reports a paragraph as its model – an APC management card reports its
        // whole firmware manifest – wrote a line across half the diagram. The full value is still on the
        // node's own data attributes, which is what the <object> wrapper exists for.
        string Line(string text, int max, string style) =>
            $"<font style=\"{style}\">{Escape(TopoLabel.Clip(text, max))}</font>";

        var sb = new StringBuilder();
        // Category first, above the name – the same order the app's map uses, so a diagram opened in
        // draw.io reads the way the screen it came from did.
        if (!string.IsNullOrEmpty(n.Kind))
            sb.Append(Line(n.Kind, TopoLabel.KindMax, "font-size:10px;opacity:0.75")).Append("<br>");
        sb.Append("<b>").Append(Escape(TopoLabel.Clip(n.Title, TopoLabel.TitleMax))).Append("</b>");
        // Vendor and model, exactly as the app's own map shows them – an exported diagram that carries less
        // than the screen it came from is the wrong thing to hand to a colleague.
        // Two lines, not one: a combined "maker + model" caption is what overflowed the box.
        if (!string.IsNullOrEmpty(n.Vendor))
            sb.Append("<br>").Append(Line(n.Vendor, TopoLabel.HardwareMax, "font-size:11px"));
        if (!string.IsNullOrEmpty(n.Model))
            sb.Append("<br>").Append(Line(n.Model, TopoLabel.HardwareMax, "font-size:11px"));
        if (!string.IsNullOrEmpty(n.Detail))
            sb.Append("<br>").Append(Line(n.Detail, TopoLabel.DetailMax, "font-size:11px"));
        if (!string.IsNullOrEmpty(n.Mac))
            sb.Append("<br>").Append(Line(n.Mac, TopoLabel.MacMax, "font-size:10px;opacity:0.8"));
        return sb.ToString();
    }

    /// <summary>Neutralises HTML metacharacters in device-supplied text. Needed because the label is HTML:
    /// a device that calls itself "&lt;b&gt;" would otherwise style the rest of the card.</summary>
    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void Edge(XmlWriter w, ref int id, string from, string to,
        HashSet<string> known, bool manualLink)
    {
        // An edge pointing at a cell that isn't there makes draw.io drop or mis-render it, so skip rather
        // than emit a dangling reference.
        if (!known.Contains(from) || !known.Contains(to)) return;

        w.WriteStartElement("mxCell");
        w.WriteAttributeString("id", "edge" + id++);
        // ⚠️ Straight connectors, not orthogonalEdgeStyle. The app draws a plain line between two nodes;
        // the right-angled routing draw.io defaults to re-routes every link around the boxes and is the
        // main reason the exported map did not look like the one on screen.
        // endArrow=none is deliberate: this map is undirected. A cable has no direction, and an arrowhead
        // would assert one that was never measured.
        w.WriteAttributeString("style",
            "edgeStyle=none;rounded=0;html=1;endArrow=none;" +
            (manualLink ? "dashed=1;strokeColor=#78909C;" : "strokeColor=#C4CCD2;"));
        w.WriteAttributeString("edge", "1");
        w.WriteAttributeString("parent", "1");
        w.WriteAttributeString("source", from);
        w.WriteAttributeString("target", to);
        w.WriteStartElement("mxGeometry");
        w.WriteAttributeString("relative", "1");
        w.WriteAttributeString("as", "geometry");
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Cell(XmlWriter w, string id, string? parent)
    {
        w.WriteStartElement("mxCell");
        w.WriteAttributeString("id", id);
        if (parent is not null) w.WriteAttributeString("parent", parent);
        w.WriteEndElement();
    }

    private static void Geometry(XmlWriter w, double x, double y, double width, double height)
    {
        w.WriteStartElement("mxGeometry");
        w.WriteAttributeString("x", N(x));
        w.WriteAttributeString("y", N(y));
        w.WriteAttributeString("width", N(width));
        w.WriteAttributeString("height", N(height));
        w.WriteAttributeString("as", "geometry");
        w.WriteEndElement();
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Cell ids must not collide with the reserved "0" and "1" layers, so everything is prefixed.
    /// The key itself is kept readable (MAC addresses and the like are fine in an XML attribute).</summary>
    private static string Id(string key) => "n_" + key;

    private static bool LooksLikeAddress(string s) =>
        s.Length > 0 && (s.Contains('.') || s.Contains(':')) && !s.Contains(' ');
}
