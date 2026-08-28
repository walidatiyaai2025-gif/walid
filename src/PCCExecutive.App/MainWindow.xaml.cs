using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PCCExecutive.App.Presentation;
using PCCExecutive.App.ViewModels;

namespace PCCExecutive.App;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ApplyAuthorityNavigation(viewModel);
        if (!viewModel.Snapshot.HasActiveRun)
            viewModel.Navigate(ScreenId.ChromeConnection);
        Closing += MainWindow_Closing;
    }

    private static void ApplyAuthorityNavigation(MainViewModel viewModel)
    {
        MoveNavigationItem(viewModel, ScreenId.ChromeConnection, 0);
        MoveNavigationItem(viewModel, ScreenId.ProjectSelection, 1);
        MoveNavigationItem(viewModel, ScreenId.Dashboard, 2);
    }

    private static void MoveNavigationItem(MainViewModel viewModel, ScreenId id, int targetIndex)
    {
        var sourceIndex = -1;
        for (var i = 0; i < viewModel.Navigation.Count; i++)
        {
            if (viewModel.Navigation[i].Id == id)
            {
                sourceIndex = i;
                break;
            }
        }

        if (sourceIndex >= 0 && sourceIndex != targetIndex)
            viewModel.Navigation.Move(sourceIndex, targetIndex);
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (DataContext is MainViewModel vm && vm.Snapshot.HasActiveRun)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
