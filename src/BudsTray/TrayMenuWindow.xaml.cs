using System.Windows;
using System.Windows.Input;

namespace RedmiBudsTray;

public partial class TrayMenuWindow : Window
{
    private readonly Action _open, _settings, _exit;
    private bool _closing;
    public TrayMenuWindow(Action open, Action settings, Action exit)
    {
        InitializeComponent();
        _open = open; _settings = settings; _exit = exit;
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
            if (e.Key is Key.Down or Key.Up)
            {
                var items = new[] { OpenItem, SettingsItem, ExitItem };
                var current = Array.FindIndex(items, item => item.IsKeyboardFocused);
                var next = current < 0 ? (e.Key == Key.Down ? 0 : 2) : (current + (e.Key == Key.Down ? 1 : 2)) % 3;
                items[next].Focus(); e.Handled = true;
            }
        };
    }
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
    private void RunAfterDismiss(Action action)
    {
        if (_closing) return;
        Dismiss();
        // Leave the menu's input/destruction stack before showing another HWND.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, action);
    }
    private void Open_Click(object sender, RoutedEventArgs e) => RunAfterDismiss(_open);
    private void Settings_Click(object sender, RoutedEventArgs e) => RunAfterDismiss(_settings);
    private void Exit_Click(object sender, RoutedEventArgs e) => RunAfterDismiss(_exit);
}
