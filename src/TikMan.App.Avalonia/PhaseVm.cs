using System;
using System.ComponentModel;
using TikMan.Core.Fleet;
using static TikMan.Core.Localization.LocalizationManager;

namespace TikMan.App.Avalonia;

/// <summary>One discovery phase, ready for the per-phase progress display: a localised name, a glyph for
/// its state and a colour. The running phase's dot pulses green, a waiting phase's dot pulses red, a
/// finished one is a steady green tick.
/// <para>A phase that was skipped is shown rather than hidden – "ZON: skipped" tells the user why no Zyxel
/// switches appeared, where an absent row tells them nothing.</para>
/// <para>⚠️ Mutable on purpose (see <see cref="Update"/>): the view model refreshes the phase list four
/// times a second during a scan, and replacing the items restarted the dots' pulse animation each time –
/// which looks like no animation at all. Updating in place keeps the animation running.</para></summary>
public sealed class PhaseVm : INotifyPropertyChanged
{
    private FleetService.ScanPhase _phase;

    public PhaseVm(FleetService.ScanPhase phase) => _phase = phase;

    public void Update(FleetService.ScanPhase phase)
    {
        var old = _phase;
        _phase = phase;
        if (old.Name != phase.Name) { Raise(nameof(Label)); Raise(nameof(BarColour)); }
        if (old.State != phase.State)
        {
            Raise(nameof(Glyph)); Raise(nameof(Colour)); Raise(nameof(Pulses)); Raise(nameof(HasProgress));
        }
        if (Math.Abs(old.Progress - phase.Progress) > 0.0005)
        {
            Raise(nameof(Progress)); Raise(nameof(HasProgress));
        }
    }

    public string Label => T("Av_Phase" + char.ToUpperInvariant(_phase.Name[0]) + _phase.Name[1..]);

    public string Glyph => _phase.State switch
    {
        FleetService.PhaseState.Done => "✓",
        FleetService.PhaseState.Skipped => "–",
        _ => "●",   // running and waiting are both a dot – the colour and the pulse tell them apart
    };

    public string Colour => _phase.State switch
    {
        FleetService.PhaseState.Done or FleetService.PhaseState.Running => "#2E7D32",
        FleetService.PhaseState.Pending => "#C0392B",
        _ => "#9E9E9E",
    };

    /// <summary>Running and waiting dots pulse (the XAML style keys the animation on this); a finished or
    /// skipped phase holds still.</summary>
    public bool Pulses => _phase.State is FleetService.PhaseState.Running or FleetService.PhaseState.Pending;

    public double Progress => _phase.Progress < 0 ? 0 : _phase.Progress;

    /// <summary>The two passes of the merged bar row: discovery runs red, the meta pass green.</summary>
    public string BarColour => _phase.Name == "probe" ? "#4CAF50" : "#E55353";

    /// <summary>Only phases that count something show a bar; the listeners run a fixed window and would
    /// have to invent a percentage.</summary>
    public bool HasProgress => _phase.Progress >= 0 && _phase.State == FleetService.PhaseState.Running;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
