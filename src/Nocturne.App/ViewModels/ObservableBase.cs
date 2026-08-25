using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nocturne.App.ViewModels;

/// <summary>
/// A minimal <see cref="INotifyPropertyChanged"/> implementation.
/// </summary>
/// <remarks>
/// Hand-written rather than generated. CommunityToolkit.Mvvm's
/// <c>[ObservableProperty]</c> on a field raises <c>MVVMTK0045</c> in a WinUI 3
/// project — the generated code is not AOT-compatible for WinRT marshalling, and
/// the documented fix is a partial property, which needs C# 13 and therefore a
/// .NET 9+ SDK. Pinning a newer SDK to save boilerplate on one view model with
/// thirteen properties is a worse trade than writing the boilerplate, and it
/// removes a source generator whose behaviour cannot be exercised on the
/// authoring machine.
/// </remarks>
public abstract class ObservableBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Assigns a backing field and raises a change notification if it moved.
    /// </summary>
    /// <returns><see langword="true"/> when the value actually changed.</returns>
    /// <remarks>
    /// The equality check is what keeps a per-tick position update from driving
    /// a layout pass when the rounded value has not moved.
    /// </remarks>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
