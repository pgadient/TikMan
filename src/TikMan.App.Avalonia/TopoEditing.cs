using System;
using System.Collections.Generic;
using System.Linq;
using TikMan.Core.Discovery;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

/// <summary>The user's own contributions to a topology map: where they moved nodes, plus any nodes and
/// connections they added by hand.
///
/// <para><b>Why this is kept apart from the map itself:</b> the physical view is built from evidence –
/// bridge forwarding tables say where a MAC was actually seen. A hand-drawn connection is an assertion.
/// Merging the two would destroy the one property that makes the map worth trusting, so manual items live
/// here, are stored separately, and are drawn dashed. A rescan replaces the measured part and leaves this
/// part alone.</para>
///
/// <para>That is also the answer to "my arrangement is thrown away on every scan": positions are keyed by
/// node, so a rebuild reuses them. Nodes that are new appear where the layout puts them; nodes that are
/// gone simply stop being looked up.</para></summary>
public sealed class TopoEditing
{
    private readonly AppData _appData;

    public TopoEditing(AppData appData) => _appData = appData;

    // ---- node positions ---------------------------------------------------------------------------

    /// <summary>The saved position for a node, or null when it has never been moved.</summary>
    public (double X, double Y)? PositionOf(string view, string key)
    {
        var p = _appData.TopoPositions.FirstOrDefault(x => x.View == view && x.Key == key);
        return p is null ? null : (p.X, p.Y);
    }

    public void SavePosition(string view, string key, double x, double y)
    {
        var p = _appData.TopoPositions.FirstOrDefault(e => e.View == view && e.Key == key);
        if (p is null)
        {
            _appData.TopoPositions.Add(new TopoNodePosition { View = view, Key = key, X = x, Y = y });
        }
        else { p.X = x; p.Y = y; }

        // A manual node's position is its own – keep the two in step so it reappears where it was left.
        var manual = _appData.TopoManualNodes.FirstOrDefault(m => m.View == view && m.Key == key);
        if (manual is not null) { manual.X = x; manual.Y = y; }
    }

    /// <summary>Forgets every saved position for a view – what "rearrange" means for hand-moved nodes.</summary>
    public void ClearPositions(string view) =>
        _appData.TopoPositions.RemoveAll(p => p.View == view);

    // ---- manual nodes and edges --------------------------------------------------------------------

    public IReadOnlyList<TopoManualNode> ManualNodes(string view) =>
        _appData.TopoManualNodes.Where(n => n.View == view).ToList();

    public IReadOnlyList<TopoManualEdge> ManualEdges(string view) =>
        _appData.TopoManualEdges.Where(e => e.View == view).ToList();

    /// <summary>Adds a node at a point on the canvas and returns its generated key.</summary>
    public string AddNode(string view, string label, double x, double y)
    {
        // Prefixed so a manual key can never collide with one the topology builder produced.
        var key = "manual:" + Guid.NewGuid().ToString("N")[..8];
        _appData.TopoManualNodes.Add(new TopoManualNode
        {
            View = view, Key = key, Label = label, X = x, Y = y,
        });
        return key;
    }

    /// <summary>Removes a manual node and every connection attached to it – a dangling edge would draw a
    /// line to nowhere.</summary>
    public void RemoveNode(string view, string key)
    {
        _appData.TopoManualNodes.RemoveAll(n => n.View == view && n.Key == key);
        _appData.TopoManualEdges.RemoveAll(e => e.View == view && (e.From == key || e.To == key));
        _appData.TopoPositions.RemoveAll(p => p.View == view && p.Key == key);
    }

    /// <summary>Connects two nodes. Either end may be a real device or a manual node. Returns false for a
    /// self-link or a duplicate (in either direction – the line is undirected).</summary>
    public bool AddEdge(string view, string from, string to)
    {
        if (from == to || from.Length == 0 || to.Length == 0) return false;
        if (_appData.TopoManualEdges.Any(e => e.View == view &&
                ((e.From == from && e.To == to) || (e.From == to && e.To == from))))
            return false;

        _appData.TopoManualEdges.Add(new TopoManualEdge { View = view, From = from, To = to });
        return true;
    }

    public void RemoveEdgesOf(string view, string key) =>
        _appData.TopoManualEdges.RemoveAll(e => e.View == view && (e.From == key || e.To == key));

    /// <summary>Drops everything the user added to a view, positions included.</summary>
    public void ClearAll(string view)
    {
        _appData.TopoManualNodes.RemoveAll(n => n.View == view);
        _appData.TopoManualEdges.RemoveAll(e => e.View == view);
        ClearPositions(view);
    }

    public bool HasAnything(string view) =>
        _appData.TopoManualNodes.Any(n => n.View == view) ||
        _appData.TopoManualEdges.Any(e => e.View == view) ||
        _appData.TopoPositions.Any(p => p.View == view);

    /// <summary>Turns a manual node into the same shape the renderer uses for measured ones, so both can be
    /// drawn by one code path. The slate fill marks it as user-added at a glance.</summary>
    public static TopoBox ToBox(TopoManualNode n) =>
        new(n.Key, "", n.Label, "", "", n.X, n.Y, 168, 56, "#37474F", "#78909C", "#FFFFFF");
}
