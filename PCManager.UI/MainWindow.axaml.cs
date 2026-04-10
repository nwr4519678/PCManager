using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PCManager.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // ── Custom titlebar — window drag ─────────────────────────────────────────
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // ── Window control buttons ────────────────────────────────────────────────
    private void CloseWindow_Click(object? sender, RoutedEventArgs e)      => Close();
    private void MinimizeWindow_Click(object? sender, RoutedEventArgs e)   => WindowState = WindowState.Minimized;
    private void MaximizeWindow_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
                         ? WindowState.Normal
                         : WindowState.Maximized;
}
