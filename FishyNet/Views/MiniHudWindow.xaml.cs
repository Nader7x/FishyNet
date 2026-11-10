using System.Windows;
using System.Windows.Input;
using FishyNet.ViewModels;

namespace FishyNet.Views;

public partial class MiniHudWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MiniHudWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        PositionAtTopRight();
    }

    private void PositionAtTopRight()
    {
        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 30;
        Top = workArea.Top + 30;
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.IsMiniHudActive = false;
        base.OnClosing(e);
    }

    private void OnRestoreFullWindowClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.IsMiniHudActive = false;
    }

    private void OnCloseHudClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.IsMiniHudActive = false;
    }
}
