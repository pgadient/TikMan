using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TikMan.Core.Discovery;
using TikMan.Core.Localization;

namespace TikMan.App.Avalonia;

/// <summary>Shows which vendor TikMan supports how far – the honest answer to "why can it do everything on
/// my MikroTik and almost nothing on that other box". The data lives in <see cref="VendorSupport"/> (Core),
/// so the matrix follows the code instead of being a screenshot that rots.</summary>
public partial class VendorMatrixWindow : Window
{
    public VendorMatrixWindow()
    {
        AvaloniaXamlLoader.Load(this);
        var grid = this.FindControl<DataGrid>("Grid");
        if (grid is not null) grid.ItemsSource = VendorSupport.Rows.Select(r => new MatrixRow(r)).ToList();

    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>A matrix row with the support levels already resolved to dot colours for the grid.</summary>
public sealed class MatrixRow
{
    private readonly VendorSupportRow _row;

    public MatrixRow(VendorSupportRow row) => _row = row;

    public string Vendor => _row.Vendor;
    public string Models => _row.Models;

    /// <summary>Shows the "work in progress" badge – the dots are today's state, not the plan.</summary>
    public bool WorkInProgress => _row.WorkInProgress;
    public string WorkInProgressText => LocalizationManager.T("Av_MatrixWip");

    // Protocols: which transport this vendor's data can actually come over.
    public MatrixCell Rest => Cell(_row.Rest);
    public MatrixCell Ssh => Cell(_row.Ssh);
    public MatrixCell Snmp => Cell(_row.Snmp);

    // Features: what TikMan can then do with it.
    public MatrixCell Monitoring => Cell(_row.Monitoring);
    public MatrixCell ConfigBackup => Cell(_row.ConfigBackup);
    public MatrixCell FullBackup => Cell(_row.FullBackup);
    public MatrixCell Updates => Cell(_row.Updates);
    public MatrixCell Topology => Cell(_row.Topology);
    public MatrixCell Logs => Cell(_row.Logs);

    private static MatrixCell Cell(SupportLevel level) => new(level);
}

/// <summary>One cell of the matrix, already resolved for the view.
///
/// <para>⚠️ The states differ by <b>shape</b>, not only by colour. "Works", "partly" and "no" are degrees of
/// the same answer and share the filled-dot shape; the other two are different kinds of answer and look
/// different: a capability the <i>hardware</i> lacks is a dash, and one that is <i>coming</i> is a hollow
/// ring. Separating five states by hue alone would be invisible to anyone who cannot tell the greys apart,
/// and hard to read for everyone else.</para></summary>
public sealed class MatrixCell
{
    private readonly SupportLevel _level;
    public MatrixCell(SupportLevel level) => _level = level;

    /// <summary>Filled dot: works, partly, or settled no.</summary>
    public bool IsDot => _level is SupportLevel.Yes or SupportLevel.Partial or SupportLevel.No;
    /// <summary>Hollow ring: on the list, not built yet – an outline is an unfilled promise.</summary>
    public bool IsPlanned => _level == SupportLevel.Planned;
    public bool IsNotAvailable => _level == SupportLevel.NotAvailable;

    public string Colour => _level switch
    {
        SupportLevel.Yes => "#4CAF50",
        SupportLevel.Partial => "#E0A33E",
        SupportLevel.Planned => "#7E9BB5",
        _ => "#B0B0B0",
    };

    public string Tip => _level switch
    {
        SupportLevel.Yes => LocalizationManager.T("Av_MatrixYes"),
        SupportLevel.Partial => LocalizationManager.T("Av_MatrixPartial"),
        SupportLevel.Planned => LocalizationManager.T("Av_MatrixPlanned"),
        SupportLevel.NotAvailable => LocalizationManager.T("Av_MatrixNotAvailable"),
        _ => LocalizationManager.T("Av_MatrixNo"),
    };
}
