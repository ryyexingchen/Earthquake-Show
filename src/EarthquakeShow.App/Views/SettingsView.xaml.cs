using System.Windows;
using System.Windows.Controls;
using EarthquakeShow.App.ViewModels;

namespace EarthquakeShow.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler? RequestClose;

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
        {
            settings.RestoreDefaults();
        }
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || settings.IsApplying)
        {
            return;
        }

        try
        {
            await settings.ApplyAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            settings.ShowError($"保存连接策略失败：{exception.Message}");
        }
    }
}
