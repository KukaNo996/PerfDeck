using System;
using System.Windows;
using System.Windows.Media;

namespace PerfDeck.Core;

/// <summary>
/// 把当前主题写进应用级资源，设置窗口、右键菜单、输入控件都通过 DynamicResource
/// 取色，切主题时整窗一起变，不需要逐个控件刷。
/// </summary>
public static class ThemeService
{
    public static void Apply(ThemeSpec t)
    {
        var r = Application.Current?.Resources;
        if (r == null) return;

        var accent = t.IsLight ? Color.FromRgb(0x02, 0x84, 0xC7) : Color.FromRgb(0x4C, 0xC9, 0xF0);
        var accentSoft = t.IsLight ? Color.FromRgb(0xE0, 0xF2, 0xFE) : Color.FromArgb(0x30, 0x4C, 0xC9, 0xF0);

        r["SetPanel"] = Brush(t.PanelTop);
        r["SetSection"] = Brush(t.CardTop);
        r["SetField"] = Brush(t.IsLight ? Colors.White : Mix(t.CardBottom, Colors.Black, 0.25));
        r["SetBorder"] = Brush(t.Border);
        r["SetBorderSoft"] = Brush(t.Separator);
        r["SetText"] = Brush(t.TextPrimary);
        r["SetSub"] = Brush(t.TextSecondary);
        r["SetDim"] = Brush(t.TextTertiary);
        r["SetTrack"] = Brush(t.Track);

        r["SetAccent"] = Brush(accent);
        r["SetAccentSoft"] = Brush(accentSoft);
        r["SetPillBg"] = Brush(t.IsLight ? Color.FromRgb(0xF1, 0xF5, 0xFA) : t.ChipBg);
        r["SetHover"] = Brush(t.IsLight ? Color.FromRgb(0xEC, 0xF3, 0xFA) : Mix(t.CardTop, Colors.White, 0.10));
        r["SetHoverBorder"] = Brush(t.IsLight ? Color.FromRgb(0xBF, 0xD2, 0xE4) : Mix(t.Border, Colors.White, 0.25));

        r["SetMenuBg"] = Brush(t.IsLight ? Colors.White : Mix(t.CardBottom, Colors.Black, 0.15));
        r["SetMenuHover"] = Brush(t.IsLight ? Color.FromRgb(0xEE, 0xF4, 0xFB) : Mix(t.CardTop, Colors.White, 0.12));
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Mix(Color a, Color b, double t)
        => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
}
