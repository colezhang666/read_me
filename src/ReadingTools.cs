using System.Runtime.InteropServices;
namespace PocketReader;

public static class ReadingTools
{
    public static readonly Dictionary<string, (string Label, Keys Key)> Shortcuts = new()
    {
        ["PreviousPage"] = ("上一页", Keys.Left), ["NextPage"] = ("下一页", Keys.Right),
        ["PreviousChapter"] = ("上一章", Keys.Control | Keys.Left), ["NextChapter"] = ("下一章", Keys.Control | Keys.Right),
        ["ScrollUp"] = ("向上移动", Keys.Up), ["ScrollDown"] = ("向下移动", Keys.Down),
        ["ChapterStart"] = ("章首", Keys.Home), ["ChapterEnd"] = ("章末", Keys.End),
        ["Bookmark"] = ("添加书签", Keys.Control | Keys.B), ["Search"] = ("打开搜索", Keys.Control | Keys.F),
        ["PreviousResult"] = ("上一个搜索结果", Keys.Shift | Keys.F3), ["NextResult"] = ("下一个搜索结果", Keys.F3),
        ["Return"] = ("返回搜索前位置", Keys.Alt | Keys.Left), ["GoChapter"] = ("跳转章节", Keys.Control | Keys.G)
    };
    public static Keys Shortcut(ReaderSettings settings, string command) => settings.NavigationHotkeys.TryGetValue(command, out int key) ? (Keys)key : Shortcuts[command].Key;
    public static int? ChapterNumber(string title)
    {
        var match = System.Text.RegularExpressions.Regex.Match(title, @"^第\s*([\d零〇一二三四五六七八九十百千万两]+)\s*[章回节]");
        if (!match.Success) return null;
        string value = match.Groups[1].Value;
        if (int.TryParse(value, out int numeric)) return numeric;
        int sum = 0, section = 0, digit = 0;
        foreach (char c in value)
        {
            int unit = c switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => 0 };
            if (unit == 10000) { sum += (section + digit) * unit; section = digit = 0; }
            else if (unit > 0) { section += (digit == 0 ? 1 : digit) * unit; digit = 0; }
            else { int index = "零一二三四五六七八九".IndexOf(c); digit = c == '两' ? 2 : c == '〇' ? 0 : index; if (digit < 0) return null; }
        }
        return sum + section + digit;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FormatRange { public IntPtr Dc, TargetDc; public Rect Area, Page; public int Start, End; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr Format(IntPtr handle, int message, IntPtr render, ref FormatRange range);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr Send(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    public static List<int> Paginate(RichTextBox layout, Size viewport)
    {
        var starts = new List<int> { 0 };
        if (layout.TextLength == 0 || viewport.Width < 4 || viewport.Height < 4) return starts;
        using var graphics = layout.CreateGraphics();
        var area = new Rect { Right = Math.Max(1, (int)((viewport.Width - 4) * 1440 / graphics.DpiX)), Bottom = Math.Max(1, (int)((viewport.Height - 4) * 1440 / graphics.DpiY)) };
        IntPtr dc = graphics.GetHdc();
        try
        {
            int start = 0;
            while (start < layout.TextLength)
            {
                var range = new FormatRange { Dc = dc, TargetDc = dc, Area = area, Page = area, Start = start, End = layout.TextLength };
                int next = Math.Min(layout.TextLength, Format(layout.Handle, 0x0439, IntPtr.Zero, ref range).ToInt32());
                // ponytail: a viewport smaller than one glyph advances one Unicode scalar; enlarge it for comfortable reading.
                if (next <= start) next = Math.Min(layout.TextLength, start + (char.IsHighSurrogate(layout.Text[start]) && start + 1 < layout.TextLength ? 2 : 1));
                if (next < layout.TextLength && char.IsLowSurrogate(layout.Text[next]) && next > start) next--;
                if (next <= start) next = Math.Min(layout.TextLength, start + 2);
                if (next < layout.TextLength) starts.Add(next);
                start = next;
            }
            return starts;
        }
        finally { Send(layout.Handle, 0x0439, IntPtr.Zero, IntPtr.Zero); graphics.ReleaseHdc(dc); }
    }
}

internal sealed class ReadingBox : RichTextBox
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Paged { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<int>? TurnPage { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<Point>? DragWindow { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<Point, int>? EdgeHit { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<int>? ResizeWindow { get; set; }
    protected override void WndProc(ref Message m)
    {
        if (Paged && m.Msg == 0x020A) { TurnPage?.Invoke((short)((long)m.WParam >> 16) > 0 ? -1 : 1); return; }
        base.WndProc(ref m);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        int hit = EdgeHit?.Invoke(PointToScreen(e.Location)) ?? 0;
        if (e.Button == MouseButtons.Left && hit != 0) { ResizeWindow?.Invoke(hit); return; }
        if (e.Button == MouseButtons.Middle) { DragWindow?.Invoke(PointToScreen(e.Location)); return; }
        base.OnMouseDown(e);
    }
}
