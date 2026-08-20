using System.Windows;

namespace EarthquakeShow.App;

/// <summary>
/// 主窗口视图。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ViewModels.MainWindowViewModel _viewModel;
    private bool _isInitialized;

    public MainWindow()
        : this(null, enableNetwork: true)
    {
    }

    public MainWindow(
        string? cachePath,
        bool enableNetwork = true)
    {
        _viewModel = new ViewModels.MainWindowViewModel(cachePath, enableNetwork);
        InitializeComponent();
        DataContext = _viewModel;
        SizeChanged += OnWindowSizeChanged;
        UpdateLayout(Width);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayout(e.NewSize.Width);
    }

    private void OnOpenDetailsClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Layout.OpenDetailsPane();
    }

    private void OnCloseDetailsClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Layout.CloseDetailsPane();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        EventListView.FocusSearch();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenSettings();
    }

    private void OnSettingsRequestClose(object? sender, EventArgs e)
    {
        _viewModel.CloseSettings();
    }

    private void OnEventListRequestOpenDetails(object? sender, EventArgs e)
    {
        _viewModel.Layout.OpenDetailsPane();
        DetailsView.FocusDetails();
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
        SizeChanged -= OnWindowSizeChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void UpdateLayout(double width)
    {
        _viewModel.Layout.UpdateWidth(width);
        DetailsColumn.Width = _viewModel.Layout.IsCompactLayout
            ? new GridLength(0)
            : new GridLength(380);
    }
}
