using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeadlockScout.App.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base. Property changes are raised on whatever thread sets them; WPF marshals
/// scalar bindings itself, and collections are only touched from the dispatcher.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
