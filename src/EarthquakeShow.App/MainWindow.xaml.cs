using System.Windows;

namespace EarthquakeShow.App;

/// <summary>
/// 主窗口视图。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ViewModels.MainWindowViewModel _viewModel = new();
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
