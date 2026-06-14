// One row in the main DataGrid. Wraps a Core.ProcStat with the formatted
// strings and binding-friendly types the GUI needs. Updated in place each
// tick so the row identity is stable (lets WPF preserve selection).

using NetPeekier.Core;

namespace NetPeekier.App.ViewModels;

public sealed class ProcessRow : ObservableObject, IProcessNode
{
    public int Pid { get; }                 // immutable; row identity

    private string _name = "";
    public string Name { get => _name; private set => SetField(ref _name, value); }

    // IProcessNode.Display: leaf rows show "PID 1234" (or the full name when
    // the group has a single member — set by the VM during reconciliation).
    private string _display = "";
    public string Display { get => _display; set => SetField(ref _display, value); }

    private string _exe = "";
    public string Exe { get => _exe; private set => SetField(ref _exe, value); }

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

    private int _connectionCount;
    public int ConnectionCount { get => _connectionCount; private set => SetField(ref _connectionCount, value); }

    public ProcessRow(int pid) { Pid = pid; }

    /// <summary>Update this row from a fresh ProcStat. Skips PropertyChanged
    /// events for fields that didn't change, which keeps WPF from re-layouting.</summary>
    public void Refresh(ProcStat p, string speedUnit)
    {
        Name            = p.Name;
        Exe             = p.Exe;
        Up              = Formatting.HumanSpeed(p.UpBps,   speedUnit);
        Down            = Formatting.HumanSpeed(p.DownBps, speedUnit);
        Total           = Formatting.HumanBytes(p.UpTotal + p.DownTotal);
        Listening       = Formatting.PortsStr(p.ListeningPorts);
        Tag             = p.Tag;
        Blocked         = p.Blocked;
        UsesWan         = p.UsesWan;
        ConnectionCount = p.Connections.Count;
    }
}
