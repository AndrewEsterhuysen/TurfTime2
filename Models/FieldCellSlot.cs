using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurfTime2.Models;

/// <summary>One cell in the Field View 4×4 pitch grid (display + drop target).</summary>
public sealed class FieldCellSlot : INotifyPropertyChanged
{
    private Player? _player;
    private double _cellSize = 52;
    private double _tokenSize = 48;
    private double _nameFontSize = 10;
    private double _timeFontSize = 8;

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

    /// <summary>Outfield cell edge length — set by GamePage responsive layout (Android CollectionView-safe).</summary>
    public double CellSize
    {
        get => _cellSize;
        set
        {
            if (Math.Abs(_cellSize - value) < 0.01) return;
            _cellSize = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Player token diameter inside this cell.</summary>
    public double TokenSize
    {
        get => _tokenSize;
        set
        {
            if (Math.Abs(_tokenSize - value) < 0.01) return;
            _tokenSize = value;
            OnPropertyChanged();
        }
    }

    public double NameFontSize
    {
        get => _nameFontSize;
        set
        {
            if (Math.Abs(_nameFontSize - value) < 0.01) return;
            _nameFontSize = value;
            OnPropertyChanged();
        }
    }

    public double TimeFontSize
    {
        get => _timeFontSize;
        set
        {
            if (Math.Abs(_timeFontSize - value) < 0.01) return;
            _timeFontSize = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Apply responsive layout metrics from Field View sizing.</summary>
    public void ApplyLayout(double cellSize, double tokenSize, double nameFontSize, double timeFontSize)
    {
        CellSize = cellSize;
        TokenSize = tokenSize;
        NameFontSize = nameFontSize;
        TimeFontSize = timeFontSize;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
