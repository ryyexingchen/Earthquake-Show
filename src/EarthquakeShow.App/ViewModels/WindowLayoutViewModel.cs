using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EarthquakeShow.App.ViewModels;

public sealed class WindowLayoutViewModel : INotifyPropertyChanged
{
    public const double CompactWidthThreshold = 1280;

    private bool _isCompactLayout;
    private bool _isDetailsPaneOpen = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set
        {
            if (_isCompactLayout == value)
            {
                return;
            }

            _isCompactLayout = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDetailsPaneVisible));
        }
    }

    public bool IsDetailsPaneOpen
    {
        get => _isDetailsPaneOpen;
        private set
        {
            if (_isDetailsPaneOpen == value)
            {
                return;
            }

            _isDetailsPaneOpen = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDetailsPaneVisible));
        }
    }

    public bool IsDetailsPaneVisible => !IsCompactLayout || IsDetailsPaneOpen;

    public void UpdateWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "窗口宽度必须是正有限值。");
        }

        bool compactLayout = width < CompactWidthThreshold;
        if (compactLayout == IsCompactLayout)
        {
            return;
        }

        IsCompactLayout = compactLayout;
        IsDetailsPaneOpen = !compactLayout;
    }

    public void OpenDetailsPane()
    {
        IsDetailsPaneOpen = true;
    }

    public void CloseDetailsPane()
    {
        if (IsCompactLayout)
        {
            IsDetailsPaneOpen = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
