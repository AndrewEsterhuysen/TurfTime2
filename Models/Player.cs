using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurfTime2.Models;

/// <summary>
/// Represents a single player in the squad, including their current position
/// and accumulated field time for this session.
/// </summary>
public sealed class Player : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private PlayerPosition _position = PlayerPosition.None;
    private int _fieldSeconds;
    private bool _isNextToRotate;
    private bool _isDragTarget;
    private bool _isDragging;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public PlayerPosition Position
    {
        get => _position;
        set
        {
            if (SetField(ref _position, value))
                OnPropertyChanged(nameof(PositionIcon));
        }
    }

    /// <summary>Accumulated seconds on field (or as goalie) during the current session.</summary>
    public int FieldSeconds
    {
        get => _fieldSeconds;
        set
        {
            if (SetField(ref _fieldSeconds, value))
                OnPropertyChanged(nameof(FieldTimeDisplay));
        }
    }

    /// <summary>Highlighted as the next player to rotate in/out.</summary>
    public bool IsNextToRotate
    {
        get => _isNextToRotate;
        set => SetField(ref _isNextToRotate, value);
    }

    /// <summary>Highlighted as the current drop target during drag-to-reorder.</summary>
    public bool IsDragTarget
    {
        get => _isDragTarget;
        set => SetField(ref _isDragTarget, value);
    }

    /// <summary>True while this row is being actively dragged by the user.</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => SetField(ref _isDragging, value);
    }

    public string PositionIcon => Position switch
    {
        PlayerPosition.Field    => "⚽",
        PlayerPosition.Bench    => "🪑",
        PlayerPosition.Goalie   => "🥅",
        PlayerPosition.Inactive => "❌",
        _                       => string.Empty
    };

    public string FieldTimeDisplay
    {
        get
        {
            var m = _fieldSeconds / 60;
            var s = _fieldSeconds % 60;
            return $"{m:D2}:{s:D2}";
        }
    }

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
