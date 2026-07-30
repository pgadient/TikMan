using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using TikMan.Core.Storage;

namespace TikMan.App.Avalonia;

/// <summary>Remembers the device grid's column widths, user-chosen order and active sort, so the list comes
/// back the way it was left. Uses the same <see cref="AppData.ColumnLayout"/> shape as the WPF client.
/// <para>Entries are positional – one per column in <b>creation</b> order, which is the order they appear in
/// XAML. ⚠️ That makes the layout tied to the column set: inserting a column shifts everything after it. So
/// a stored layout whose length no longer matches is discarded rather than applied to the wrong columns,
/// which would silently hand the user a grid with the wrong widths and a sort on the wrong field.</para></summary>
public static class GridLayout
{
    // Shadow copy of the active sort, PER GRID. See Track().
    // ⚠️ Keyed by grid, not a single pair of fields: the IPv4 and IPv6 views are both tracked, and one
    // shared pair would let whichever was sorted last overwrite the other's stored sort.
    private static readonly Dictionary<DataGrid, (int Column, bool Descending)> Sorts = new();

    /// <summary>Which of the two column layouts a grid uses. They have different columns, so they cannot
    /// share a slot.</summary>
    public enum Slot { Devices, Ipv6 }

    /// <summary>Generation of the column set. Raise this whenever the columns change in a way that makes
    /// stored pixel widths wrong – a new column, a removed one, or a switch to content-based sizing.
    /// A layout from any other generation is discarded rather than applied.</summary>
    private const int CurrentVersion = 4;

    /// <summary>The column that exists only to soak up the leftover width. Never sized, never reordered –
    /// it has no content and its position is what makes it work.</summary>
    private static bool IsFiller(DataGridColumn column) => column.Tag as string == "filler";

    /// <summary>Starts following the grid's sorting so it can be saved later.
    /// <para>⚠️ Necessary because the sort is write-only from the outside: <c>Column.Sort(direction)</c> is
    /// public but nothing exposes the current direction. So this mirrors the control's own toggle rule –
    /// a fresh column sorts ascending, the same column again flips – which is what the DataGrid does when
    /// a header is clicked.</para>
    /// <para>The consequence of that indirection: if Avalonia ever changes its click behaviour, the
    /// restored direction can be wrong. That is the whole cost, and it is one click to correct.</para></summary>
    public static void Track(DataGrid grid)
    {
        grid.Sorting += (_, e) =>
        {
            var index = grid.Columns.IndexOf(e.Column);
            if (index < 0) return;
            var prev = Sorts.TryGetValue(grid, out var s) ? s : (-1, false);
            Sorts[grid] = (index, index == prev.Item1 && !prev.Item2);
        };
    }

    /// <summary>Applies a stored layout, if one fits this grid.</summary>
    public static void Restore(DataGrid grid, AppData appData, Slot slot = Slot.Devices)
    {
        try
        {
            var saved = slot == Slot.Ipv6 ? appData.Ipv6ColumnLayout : appData.ColumnLayout;
            var version = slot == Slot.Ipv6 ? appData.Ipv6ColumnLayoutVersion : appData.ColumnLayoutVersion;
            var sortColumn = slot == Slot.Ipv6 ? appData.Ipv6SortColumn : appData.SortColumn;
            var sortDesc = slot == Slot.Ipv6 ? appData.Ipv6SortDescending : appData.SortDescending;
            // ⚠️ Version first, count second. A stored layout is a list of pixel widths, and applying it
            // pins every column – which silently overrides content-based sizing however the columns are
            // declared. That is exactly what happened when the grid moved to Auto + MaxWidth: the columns
            // kept the narrow pixel widths from an earlier session and clipped their contents, and the count
            // check could not see it because the number of columns had not changed.
            // ⚠️ Tied to "keep the device list": remembering column widths is remembering something about
            // a list the user asked not to keep. Someone who starts each session from an empty list gets
            // fresh, content-sized columns rather than widths measured against devices that are gone.
            if (!appData.PersistDeviceList) return;

            if (version != CurrentVersion) return;
            if (saved.Count != grid.Columns.Count) return;   // column set changed – see the class remarks

            for (var i = 0; i < grid.Columns.Count; i++)
            {
                var col = grid.Columns[i];
                var state = saved[i];

                // ⚠️ The filler column keeps its star width, always. Its whole job is to claim whatever
                // space is left over so that every point inside a row belongs to some cell – pin it to a
                // pixel width from a previous session and the strip to the right of the last column stops
                // being part of any row again, and clicking there selects nothing.
                if (IsFiller(col)) continue;

                if (state.Width > 0) col.Width = new DataGridLength(state.Width, DataGridLengthUnitType.Pixel);
                if (state.DisplayIndex >= 0 && state.DisplayIndex < grid.Columns.Count)
                    col.DisplayIndex = state.DisplayIndex;
            }

            if (sortColumn >= 0 && sortColumn < grid.Columns.Count)
            {
                grid.Columns[sortColumn].Sort(
                    sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending);
                // Seed the shadow copy, so saving without touching a header keeps what was restored.
                Sorts[grid] = (sortColumn, sortDesc);
            }
        }
        catch { /* a layout that won't apply is not worth failing startup over */ }
    }

    /// <summary>Captures the current layout into the settings object. The caller decides when to save.</summary>
    public static void Capture(DataGrid grid, AppData appData, Slot slot = Slot.Devices)
    {
        try
        {
            // Same condition as Restore – no point writing a layout that will never be read back.
            if (!appData.PersistDeviceList) return;

            var states = new List<ColumnState>(grid.Columns.Count);
            foreach (var col in grid.Columns)
            {
                states.Add(new ColumnState
                {
                    // ActualWidth is what the user actually sees; Width may still be "Auto"/star.
                    // ⚠️ 0 for the filler, so a later Restore has nothing to pin it with even if the skip
                    // above were ever removed – its width has to stay computed, never remembered.
                    Width = !IsFiller(col) && col.ActualWidth > 0 ? col.ActualWidth : 0,
                    DisplayIndex = col.DisplayIndex,
                });
            }
            // ⚠️ The sort comes from our own tracking, not from the grid. Avalonia's DataGridColumn can be
            // told to sort but not asked what it is sorted by: no public SortDirection getter, and
            // GetSortDescription() is internal. So Track() shadows the Sorting event instead.
            var (sortColumn, sortDesc) = Sorts.TryGetValue(grid, out var s) ? s : (-1, false);

            if (slot == Slot.Ipv6)
            {
                appData.Ipv6ColumnLayout = states;
                appData.Ipv6ColumnLayoutVersion = CurrentVersion;
                appData.Ipv6SortColumn = sortColumn;
                appData.Ipv6SortDescending = sortDesc;
            }
            else
            {
                appData.ColumnLayout = states;
                appData.ColumnLayoutVersion = CurrentVersion;
                appData.SortColumn = sortColumn;
                appData.SortDescending = sortDesc;
            }
        }
        catch { /* ditto – never let bookkeeping break closing the window */ }
    }
}
