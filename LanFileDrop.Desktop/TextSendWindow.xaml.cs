using System.Windows;

namespace LanFileDrop.Desktop;

public partial class TextSendWindow : Window
{
    public TextSendWindow() => InitializeComponent();
    public string MessageText => MessageBox.Text;
    private void Send_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
