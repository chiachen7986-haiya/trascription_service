using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using BlueJays.App.Localization;

namespace BlueJays.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    protected ObservableObject() => WeakEventManager<Localizer, EventArgs>.AddHandler(Localizer.Current,
        nameof(Localizer.LanguageChanged), (_, _) => Raise(string.Empty));
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(property);
        return true;
    }
    protected void Raise([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
