using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurfTime2.Models;

/// <summary>
/// Sentinel item that represents the collapsed/expanded header row for
/// inactive (absent) players in the swipeable roster CollectionView.
/// </summary>
public sealed class InactiveGroupHeader : INotifyPropertyChanged
{
    private bool _isExpanded;
    private int  _count;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandIcon));
                OnPropertyChanged(nameof(Label));
            }
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (SetField(ref _count, value))
                OnPropertyChanged(nameof(Label));
        }
    }

    public string ExpandIcon => IsExpanded ? "▲" : "▼";

    public string Label => $"❌  Absent / Inactive  ({Count})  {ExpandIcon}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
