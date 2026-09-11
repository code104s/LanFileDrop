using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace LanFileDrop.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && DataContext is MainViewModel { SelectedDevice: not null }
            ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Opacity = e.Effects == DragDropEffects.Copy ? 0.65 : 1;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        DropZone.Opacity = 1;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || DataContext is not MainViewModel viewModel) return;
        try { await viewModel.SendPathsAsync(paths); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "LanFileDrop", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.IsHistoryOpen = false;
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text } || string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            if (sender is Button button)
            {
                button.Content = "✓ Đã sao chép";
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (_, _) => { timer.Stop(); button.Content = "Sao chép"; };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể sao chép: {ex.Message}", "LanFileDrop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
