using System.Windows;
namespace LiveBoost;

/// <summary>Small always-on-top stats box. Drag it with the mouse. It does not inject into games, so it shows over windowed/borderless games only.</summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
    }
    public void SetText(string t) => Info.Text = t;
}
