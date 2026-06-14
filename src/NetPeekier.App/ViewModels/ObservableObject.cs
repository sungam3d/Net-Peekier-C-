// Tiny ObservableObject + RelayCommand. We don't pull in
// CommunityToolkit.Mvvm to keep the offline build story simple; the
// surface here matches the same API so swapping in later is mechanical.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace NetPeekier.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Set <paramref name="field"/> to <paramref name="value"/> and raise
    /// <see cref="PropertyChanged"/> if the value actually changed. Returns
    /// true if it changed (handy for chained logic).
    /// </summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _exec;
    private readonly Func<bool>? _can;

    public RelayCommand(Action exec, Func<bool>? can = null) { _exec = exec; _can = can; }

    public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;
    public void Execute(object? parameter) => _exec();

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _exec;
    private readonly Predicate<T?>? _can;

    public RelayCommand(Action<T?> exec, Predicate<T?>? can = null) { _exec = exec; _can = can; }

    public bool CanExecute(object? parameter) =>
        _can?.Invoke(parameter is T t ? t : default) ?? true;

    public void Execute(object? parameter) => _exec(parameter is T t ? t : default);

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
