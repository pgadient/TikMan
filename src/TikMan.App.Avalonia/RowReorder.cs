using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace TikMan.App.Avalonia;

/// <summary>Drag-to-reorder for a <see cref="DataGrid"/> bound to an <see cref="ObservableCollection{T}"/>, plus
/// the shared reorder logic the ▲▼ buttons use.
///
/// <para>The backup and update assistants run their devices in grid order, so the order is a real setting – set
/// by dragging a row, or with the ▲▼ buttons. Dragging shows a blue insertion line between rows and only moves
/// the row on release (drop), so the list does not shuffle under the cursor.</para>
///
/// <para>Three Avalonia traps, all measured:</para>
/// <list type="number">
/// <item>⚠️ <b>The grid ignores <see cref="ObservableCollection{T}.Move"/></b> (only Add/Remove/Reset redraw),
/// so a move is a Remove followed by an Insert.</item>
/// <item>⚠️ <b>The grid captures the pointer on press</b>, so during a drag <c>e.Source</c> is stuck on the
/// anchor row. The target is found by the pointer's Y position (the row bands), never from <c>e.Source</c>.</item>
/// <item>⚠️ <b>A checkbox-cell click does not select the row</b>, so the pressed row is selected explicitly –
/// otherwise the ▲▼ buttons, which act on the selected row, have no target.</item>
/// </list></summary>
public static class RowReorder
{
    public static void Enable<T>(DataGrid grid, ObservableCollection<T> rows) where T : class
    {
        T? dragItem = null;
        bool dragging = false;
        Point start = default;
        Border? divider = null;
        int dropIndex = -1;

        void RemoveDivider()
        {
            if (divider is null) return;
            AdornerLayer.GetAdornerLayer(grid)?.Children.Remove(divider);
            divider = null;
        }

        void ShowDivider(double y)
        {
            var layer = AdornerLayer.GetAdornerLayer(grid);
            if (layer is null) return;
            if (divider is null)
            {
                divider = new Border
                {
                    Height = 3,
                    Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x80, 0xD6)),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(1.5),
                    IsHitTestVisible = false,
                };
                layer.Children.Add(divider);
                AdornerLayer.SetAdornedElement(divider, grid);
            }
            divider.Margin = new Thickness(0, y - 1.5, 0, 0);
        }

        void End()
        {
            dragItem = null; dragging = false; dropIndex = -1;
            RemoveDivider();
        }

        grid.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            End();
            if (e.Source is not Visual v || FindRow(v)?.DataContext is not T item) return;
            grid.SelectedItem = item;                       // so the ▲▼ buttons have a target
            if (!IsInteractive(v)) { dragItem = item; start = e.GetPosition(grid); }
        }, RoutingStrategies.Tunnel);

        grid.AddHandler(InputElement.PointerMovedEvent, (s, e) =>
        {
            if (dragItem is null) return;
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) { End(); return; }
            var pos = e.GetPosition(grid);
            if (!dragging && Math.Abs(pos.Y - start.Y) < 6) return;   // click, not a drag
            dragging = true;
            if (ComputeDrop(grid, rows, pos) is { } d) { dropIndex = d.Index; ShowDivider(d.Y); }
        }, RoutingStrategies.Tunnel);

        void Release(object? s, RoutedEventArgs e)
        {
            if (dragging && dragItem is not null && dropIndex >= 0)
                DoDrop(grid, rows, dragItem, dropIndex);
            End();
        }
        grid.AddHandler(InputElement.PointerReleasedEvent, Release, RoutingStrategies.Tunnel);
        grid.AddHandler(InputElement.PointerCaptureLostEvent, (s, e) => End(), RoutingStrategies.Tunnel);
    }

    /// <summary>Moves the grid's selected row by one place (the ▲▼ buttons). Remove+Insert, because the grid
    /// ignores a Move.</summary>
    public static void MoveSelected<T>(DataGrid grid, ObservableCollection<T> rows, int delta) where T : class
    {
        if (grid.SelectedItem is not T row) return;
        int from = rows.IndexOf(row);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= rows.Count) return;
        rows.RemoveAt(from);
        rows.Insert(to, row);
        grid.SelectedItem = row;
    }

    /// <summary>The insertion the divider marks: the list index the dragged row would take, and the Y (in grid
    /// coordinates) of the line to draw. Index is in the list AS IT IS (dragged row still in place); DoDrop
    /// adjusts for the removal.</summary>
    private static (int Index, double Y)? ComputeDrop<T>(DataGrid grid, ObservableCollection<T> rows, Point pos)
        where T : class
    {
        var ordered = grid.GetVisualDescendants().OfType<DataGridRow>()
            .Select(r => (Row: r, Top: r.TranslatePoint(new Point(0, 0), grid)?.Y))
            .Where(x => x.Top is not null && x.Row.DataContext is T)
            .OrderBy(x => x.Top!.Value)
            .ToList();
        if (ordered.Count == 0) return null;
        foreach (var (row, top) in ordered)
        {
            double t = top!.Value, h = row.Bounds.Height, b = t + h;
            int i = rows.IndexOf((T)row.DataContext!);
            if (pos.Y < t + h / 2) return (i, t);       // upper half (or above) → line above this row
            if (pos.Y < b) return (i + 1, b);           // lower half → line below this row
        }
        // Below every row → drop at the end, line under the last row.
        var last = ordered[^1];
        return (rows.Count, last.Top!.Value + last.Row.Bounds.Height);
    }

    private static void DoDrop<T>(DataGrid grid, ObservableCollection<T> rows, T item, int insertIndex)
        where T : class
    {
        int from = rows.IndexOf(item);
        if (from < 0) return;
        if (insertIndex > from) insertIndex--;          // the removal shifts everything after `from` down one
        insertIndex = Math.Clamp(insertIndex, 0, rows.Count - 1);
        if (insertIndex == from) return;
        rows.RemoveAt(from);
        rows.Insert(insertIndex, item);
        grid.SelectedItem = item;
    }

    private static DataGridRow? FindRow(Visual? v)
    {
        while (v is not null and not DataGridRow) v = v.GetVisualParent();
        return v as DataGridRow;
    }

    private static bool IsInteractive(Visual? v)
    {
        while (v is not null and not DataGridRow)
        {
            if (v is CheckBox or ComboBox or Button) return true;
            v = v.GetVisualParent();
        }
        return false;
    }
}
