using System.Windows;
using System.Windows.Input;
using PerfDeck.Core;

namespace PerfDeck.UI;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(SensorService sensors)
    {
        InitializeComponent();
        BtnClose.Click += (_, _) => Close();
        TxtDump.Text = sensors.Describe();
        Loaded += (_, _) => TxtDump.Text = sensors.Describe();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }
}
