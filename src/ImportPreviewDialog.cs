using System.Text.RegularExpressions;
namespace PocketReader;

public sealed class ImportOptions
{
    public string EncodingName { get; }
    public string Rule { get; }
    public string? Pattern { get; }
    public Regex? HeadingRegex { get; }
    public ImportOptions(string encodingName = "自动", string rule = "自动识别", string pattern = "")
    {
        EncodingName = encodingName; Rule = rule;
        if (rule == "自定义正则")
        {
            if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("请输入章节标题的正则表达式。");
            Pattern = pattern;
            HeadingRegex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
    }
}

internal sealed class ImportPreviewDialog : Form
{
    private readonly ComboBox encoding = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox rule = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox pattern = new() { Dock = DockStyle.Top, PlaceholderText = "自定义章节正则，例如：^Chapter\\s+\\d+.*$" };
    private readonly ListBox chapterList = new() { Dock = DockStyle.Fill };
    private readonly TextBox excerpt = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 30, Text = "选择编码和规则，然后预览目录。" };
    private readonly Button accept = new() { Text = "确认导入", Enabled = false, DialogResult = DialogResult.OK, AutoSize = true };
    private readonly string path;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "ReadMePreview-" + Guid.NewGuid().ToString("N"));
    private CancellationTokenSource? cancellation;
    private bool running;
    private BookRecord? preview;
    public ImportOptions Options { get; private set; } = new();

    public ImportPreviewDialog(string path)
    {
        this.path = path;
        Text = "导入预览 · " + Path.GetFileName(path);
        ClientSize = new Size(760, 520); StartPosition = FormStartPosition.CenterParent;
        encoding.Items.AddRange(["自动", "utf-8", "gb18030", "utf-16", "utf-16BE", "big5"]); encoding.SelectedIndex = 0;
        rule.Items.AddRange(["自动识别", "自定义正则", "不分章"]); rule.SelectedIndex = 0;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var refresh = new Button { Text = "预览目录", AutoSize = true };
        refresh.Click += async (_, _) => await RefreshPreview();
        top.Controls.AddRange([new Label { Text = "编码", AutoSize = true }, encoding, new Label { Text = "章节规则", AutoSize = true }, rule, refresh]);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 300 };
        split.Panel1.Controls.Add(chapterList); split.Panel2.Controls.Add(excerpt);
        chapterList.SelectedIndexChanged += (_, _) =>
        {
            if (preview != null && chapterList.SelectedIndex >= 0)
            {
                string text = File.ReadAllText(Path.Combine(scratch, preview.Id, preview.Chapters[chapterList.SelectedIndex].FileName));
                excerpt.Text = text[..Math.Min(4000, text.Length)];
            }
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(accept); buttons.Controls.Add(new Button { Text = "取消", DialogResult = DialogResult.Cancel });
        Controls.Add(split); Controls.Add(pattern); Controls.Add(top); Controls.Add(status); Controls.Add(buttons);
        encoding.SelectedIndexChanged += (_, _) => accept.Enabled = false;
        rule.SelectedIndexChanged += (_, _) => { accept.Enabled = false; pattern.Enabled = rule.Text == "自定义正则"; };
        pattern.TextChanged += (_, _) => accept.Enabled = false;
        pattern.Enabled = false;
        Shown += async (_, _) => await RefreshPreview();
        FormClosing += (_, _) => cancellation?.Cancel();
    }

    private async Task RefreshPreview()
    {
        cancellation?.Cancel();
        if (running) { status.Text = "正在取消上次预览，请稍后再点击预览。"; return; }
        try
        {
            Options = new ImportOptions(encoding.Text, rule.Text, pattern.Text);
            running = true; accept.Enabled = false;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            encoding.Enabled = rule.Enabled = pattern.Enabled = false;
            status.Text = "正在分析文件…";
            var book = await Task.Run(() => TxtImporter.Import(path, scratch, token: token, options: Options));
            if (IsDisposed || token.IsCancellationRequested) return;
            preview = book; chapterList.Items.Clear(); chapterList.Items.AddRange(book.Chapters.ToArray());
            if (chapterList.Items.Count > 0) chapterList.SelectedIndex = 0;
            status.Text = $"识别到 {book.Chapters.Count} 个章节，可选择章节检查正文。";
            accept.Enabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) status.Text = "预览失败：" + ex.Message; }
        finally
        {
            running = false;
            if (IsDisposed) Cleanup();
            else { encoding.Enabled = rule.Enabled = true; pattern.Enabled = rule.Text == "自定义正则"; }
        }
    }
    private void Cleanup() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    protected override void Dispose(bool disposing) { if (disposing) { cancellation?.Cancel(); if (!running) Cleanup(); } base.Dispose(disposing); }
}
