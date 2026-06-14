// A parent node in the process tree: groups all PIDs that share an
// executable name (the classic svchost.exe cluster). Holds aggregate
// up/down/total figures and an observable child collection of ProcessRow.
//
// Both ProcessGroup and ProcessRow implement IProcessNode so the TreeView's
// columns bind to one shape regardless of node kind. Groups are kept in
// place and reconciled by name each tick (like the flat list was reconciled
// by PID) so expand/collapse and selection survive refreshes.

using System.Collections.ObjectModel;
using NetPeekier.Core;

namespace NetPeekier.App.ViewModels;

/// <summary>Common surface for tree rows so one set of columns binds to both.</summary>
public interface IProcessNode
{
    string Display   { get; }   // name (group) or "PID 1234" (leaf)
    string Up        { get; }
    string Down      { get; }
    string Total     { get; }
    string Listening { get; }
    string Tag       { get; }
    bool   Blocked   { get; }
    bool   UsesWan   { get; }
}

public sealed class ProcessGroup : ObservableObject, IProcessNode
{
    public string Name { get; }     // immutable; group identity

    public ObservableCollection<ProcessRow> Children { get; } = new();

    private string _display = "";
    public string Display { get => _display; private set => SetField(ref _display, value); }

    private string _up = "0 B/s";
    public string Up { get => _up; private set => SetField(ref _up, value); }

    private string _down = "0 B/s";
    public string Down { get => _down; private set => SetField(ref _down, value); }

    private string _total = "0 B";
    public string Total { get => _total; private set => SetField(ref _total, value); }

    private string _listening = "";
    public string Listening { get => _listening; private set => SetField(ref _listening, value); }

    private string _tag = "";
    public string Tag { get => _tag; private set => SetField(ref _tag, value); }

    private bool _blocked;
    public bool Blocked { get => _blocked; private set => SetField(ref _blocked, value); }

    private bool _usesWan;
    public bool UsesWan { get => _usesWan; private set => SetField(ref _usesWan, value); }

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }

    /// <summary>True when the group has exactly one member (UI may flatten label).</summary>
    private bool _single;
    public bool Single { get => _single; private set => SetField(ref _single, value); }

    public ProcessGroup(string name) { Name = name; Display = name; }

    /// <summary>
    /// Recompute the aggregate columns from the member ProcStats. Caller has
    /// already refreshed the child ProcessRows; this just sums.
    /// </summary>
    public void RefreshAggregate(IReadOnlyList<ProcStat> members, string speedUnit)
    {
        double up = 0, down = 0;
        double tup = 0, tdown = 0;
        bool blocked = false, usesWan = false;
        string tag = "";
        var ports = new SortedSet<int>();

        foreach (var m in members)
        {
            up    += m.UpBps;
            down  += m.DownBps;
            tup   += m.UpTotal;
            tdown += m.DownTotal;
            blocked |= m.Blocked;
            usesWan |= m.UsesWan;
            if (tag.Length == 0 && !string.IsNullOrEmpty(m.Tag)) tag = m.Tag;
            foreach (var pt in m.ListeningPorts) ports.Add(pt);
        }

        Display   = Name;
        Up        = Formatting.HumanSpeed(up, speedUnit);
        Down      = Formatting.HumanSpeed(down, speedUnit);
        Total     = Formatting.HumanBytes(tup + tdown);
        Listening = Formatting.PortsStr(ports.ToList());
        Tag       = tag;
        Blocked   = blocked;
        UsesWan   = usesWan;
        Single    = members.Count == 1;
    }
}
