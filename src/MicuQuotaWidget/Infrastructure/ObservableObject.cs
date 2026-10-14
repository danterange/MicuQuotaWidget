using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MicuQuotaWidget.Infrastructure;

/// <summary>
/// Provides minimal property-change notification so the WPF view updates without taking a third-party MVVM dependency.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>
    /// Notifies bindings after a public view-model property changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Updates a backing field only when its value changed, avoiding redundant binding and layout work during polling.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Raises a property notification for a derived property whose displayed value changed indirectly.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
