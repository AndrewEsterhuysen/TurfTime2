using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurfTime2.Models;

/// <summary>One cell in the Field View 4×4 pitch grid (display + drop target).</summary>
public sealed class FieldCellSlot : INotifyPropertyChanged
{
    private Player? _player;

    public FieldCellSlot(int cellNumber)
    {
        CellNumber = cellNumber;
    }

    /// <summary>1–16, row-major, top-left = 1.</summary>
    public int CellNumber { get; }

    public string CellLabel => CellNumber.ToString();

    public Player? Player
    {
        get => _player;
        set
        {
            if (ReferenceEquals(_player, value)) return;
            _player = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPlayer));
        }
    }

    public bool HasPlayer => Player is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
