using System;
using System.Linq;
using System.Windows.Media;

namespace PerfDeck.Core;

/// <summary>一套配色。卡片与面板都从这里取色，切换主题时整体重刷。</summary>
public sealed class ThemeSpec
{
    public string Id = "";
    public string Name = "";
    public bool IsLight;

    /// <summary>面板渐变（上浅下深，让主题色调更明显）</summary>
    public Color PanelTop;
    public Color PanelBottom;

    public Color CardTop;
    public Color CardBottom;
    public Color Border;
    public Color TextPrimary;
    public Color TextSecondary;
    public Color TextTertiary;
    public Color ChipBg;
    public Color ChipBorder;
    public Color Track;
    public Color WarnText;
    public Color CritText;
    public Color Separator;
}

public static class Themes
{
    public static readonly ThemeSpec[] All =
    {
        new()
        {
            Id = "obsidian", Name = "深空蓝",
            PanelTop = Hex("#0D1B2A"), PanelBottom = Hex("#060C14"),
            CardTop = Hex("#18293C"), CardBottom = Hex("#0C1620"),
            Border = Hex("#27435C"), Separator = Hex("#152838"),
            TextPrimary = Hex("#E6F0FA"), TextSecondary = Hex("#8FA6BC"), TextTertiary = Hex("#5C7186"),
            ChipBg = Hex("#152738"), ChipBorder = Hex("#294558"), Track = Hex("#1B2E42"),
            WarnText = Hex("#FBBF24"), CritText = Hex("#F87171")
        },
        new()
        {
            Id = "graphite", Name = "石墨黑",
            PanelTop = Hex("#101214"), PanelBottom = Hex("#050607"),
            CardTop = Hex("#1A1C1F"), CardBottom = Hex("#0C0E10"),
            Border = Hex("#2E3236"), Separator = Hex("#15171A"),
            TextPrimary = Hex("#F2F4F7"), TextSecondary = Hex("#909599"), TextTertiary = Hex("#5F6468"),
            ChipBg = Hex("#191B1E"), ChipBorder = Hex("#2B2F33"), Track = Hex("#1E2124"),
            WarnText = Hex("#FCD34D"), CritText = Hex("#FB7185")
        },
        new()
        {
            Id = "aurora", Name = "极光绿",
            PanelTop = Hex("#062018"), PanelBottom = Hex("#020C0A"),
            CardTop = Hex("#103028"), CardBottom = Hex("#071917"),
            Border = Hex("#1F5A48"), Separator = Hex("#0F3227"),
            TextPrimary = Hex("#E2F8F1"), TextSecondary = Hex("#86AFA4"), TextTertiary = Hex("#4F7268"),
            ChipBg = Hex("#123529"), ChipBorder = Hex("#215845"), Track = Hex("#164034"),
            WarnText = Hex("#FBBF24"), CritText = Hex("#FB7185")
        },
        new()
        {
            Id = "nebula", Name = "星云紫",
            PanelTop = Hex("#170F28"), PanelBottom = Hex("#08050F"),
            CardTop = Hex("#2A1B42"), CardBottom = Hex("#130C20"),
            Border = Hex("#41305F"), Separator = Hex("#241839"),
            TextPrimary = Hex("#F1EAFB"), TextSecondary = Hex("#A392BE"), TextTertiary = Hex("#6B5C82"),
            ChipBg = Hex("#261A3B"), ChipBorder = Hex("#402F5C"), Track = Hex("#2B1F40"),
            WarnText = Hex("#FBBF24"), CritText = Hex("#F87171")
        },
        new()
        {
            Id = "ember", Name = "暮橙",
            PanelTop = Hex("#231408"), PanelBottom = Hex("#0D0704"),
            CardTop = Hex("#3A2314"), CardBottom = Hex("#1C1009"),
            Border = Hex("#5E3A22"), Separator = Hex("#33200F"),
            TextPrimary = Hex("#FBF0E6"), TextSecondary = Hex("#C0A08A"), TextTertiary = Hex("#84695A"),
            ChipBg = Hex("#36200F"), ChipBorder = Hex("#58341C"), Track = Hex("#3B2414"),
            WarnText = Hex("#FCD34D"), CritText = Hex("#F87171")
        },
        new()
        {
            Id = "daylight", Name = "日光",
            IsLight = true,
            PanelTop = Hex("#F7F9FC"), PanelBottom = Hex("#E8EDF4"),
            CardTop = Hex("#FFFFFF"), CardBottom = Hex("#F4F7FB"),
            Border = Hex("#D3DCE7"), Separator = Hex("#E2E8F0"),
            TextPrimary = Hex("#16202B"), TextSecondary = Hex("#5C6B7C"), TextTertiary = Hex("#8B98A8"),
            ChipBg = Hex("#EFF4FA"), ChipBorder = Hex("#DCE3EC"), Track = Hex("#E2E8F0"),
            WarnText = Hex("#B45309"), CritText = Hex("#DC2626")
        }
    };

    public static ThemeSpec Find(string? id)
        => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    private static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }
}
