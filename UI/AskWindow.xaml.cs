using System.Windows;
using System.Windows.Input;

namespace PerfDeck.UI;

/// <summary>轻量确认弹窗（暗色、无边框），用于运行时征询定位权限。</summary>
public partial class AskWindow : Window
{
    public bool Allowed { get; private set; }

    public AskWindow(string title, string message, string allowText = "允许", string denyText = "不允许")
    {
        InitializeComponent();
        TxtTitle.Text = title;
        TxtMessage.Text = message;
        BtnAllow.Content = allowText;
        BtnDeny.Content = denyText;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } }
        };
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        Allowed = true;
        DialogResult = true;
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        Allowed = false;
        DialogResult = false;
    }

    public static bool Ask(Window? owner, string title, string message,
                           string allowText = "允许", string denyText = "不允许")
    {
        var w = new AskWindow(title, message, allowText, denyText);
        if (owner != null && owner.IsLoaded) w.Owner = owner;
        return w.ShowDialog() == true;
    }
}
