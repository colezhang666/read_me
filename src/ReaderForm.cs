using System.Drawing;
using System.Text;

namespace PocketReader;

public sealed class ReaderForm : Form
{
    private readonly ReaderSettings settings;
    private readonly TabControl sidebarTabs = new() { Dock = DockStyle.Fill, Width = 285 };
    private readonly SplitContainer mainSplit = new() { Dock = DockStyle.Fill, SplitterDistance = 285, FixedPanel = FixedPanel.Panel1 };
    private FlowLayoutPanel? toolbar;
    private bool focusMode;
    private bool sidebarWasCollapsed;
    private Rectangle previousBounds;
    private FormWindowState previousWindowState;
    private FormBorderStyle previousBorderStyle;
    private Label? readerTitle;
    private readonly Button directoryButton = new() { Text = "收起目录", AutoSize = true };
    private readonly ListBox books = new() { Dock = DockStyle.Fill };
    private readonly ListBox chapters = new() { Dock = DockStyle.Fill };
    private readonly RichTextBox reader = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DetectUrls = false, ReadOnly = true, HideSelection = false };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly ComboBox theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly NumericUpDown fontSize = new() { Minimum = 1, Maximum = 32, Value = 17, Width = 65 };
    private readonly TextBox searchBox = new() { Width = 240, PlaceholderText = "搜索全书关键词" };
    private readonly ListBox results = new() { Dock = DockStyle.Fill };
    private readonly ListBox bookmarks = new() { Dock = DockStyle.Fill };
    private BookRecord? currentBook;
    private int currentChapter = -1;
    private CancellationTokenSource? searchCancellation;
    private bool loading;
    private readonly NotifyIcon trayIcon = new();
    private readonly Button hideButton = new() { Text = "隐藏窗口", AutoSize = true };
    private readonly Panel readerPanel = new() { Dock = DockStyle.Fill };
    private Font? readingFont;
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 600 };
    private bool saveFailed;
    private bool importing;
    private bool closing;

    public ReaderForm()
    {
        Text = "Read_me";
        Width = 1260; Height = 820; MinimumSize = Size.Empty;
        StartPosition = FormStartPosition.CenterScreen;
        settings = LibraryStore.Load();
        if (string.IsNullOrWhiteSpace(settings.Theme)) settings.Theme = "浅色";
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            Keys pressed = e.KeyCode | e.Modifiers;
            if (pressed == (Keys)settings.HideWindowHotkey) { HideToTray(); e.SuppressKeyPress = true; }
            else if (pressed == (Keys)settings.FocusModeHotkey) { ToggleFocusMode(); e.SuppressKeyPress = true; }
            else if (pressed == (Keys)settings.DirectoryHotkey) { ToggleDirectoryDrawer(); e.SuppressKeyPress = true; }
            else if (focusMode && e.KeyCode == Keys.Escape) { ToggleFocusMode(); e.SuppressKeyPress = true; }
        };
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Text = "Read_me";
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        trayIcon.ContextMenuStrip = new ContextMenuStrip();
        trayIcon.ContextMenuStrip.Items.Add("显示窗口", null, (_, _) => RestoreFromTray());
        trayIcon.ContextMenuStrip.Items.Add("退出", null, (_, _) => { trayIcon.Visible = false; Application.Exit(); });
        BuildUi();
        RestoreWindow();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Persist(); };
        LoadBooks();
        Shown += (_, _) =>
        {
            RestoreReadingPosition();
            if (LibraryStore.RecoveryNotice != null) MessageBox.Show(this, LibraryStore.RecoveryNotice, "存档恢复");
        };
        FormClosing += (_, e) =>
        {
            if (importing) { e.Cancel = true; MessageBox.Show(this, "正在导入，请等待导入完成后退出。", "Read_me"); return; }
            SaveState();
            if (saveFailed) { e.Cancel = true; return; }
            closing = true;
            searchCancellation?.Cancel();
            saveTimer.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
        };
        FormClosed += (_, _) => readingFont?.Dispose();
    }

    private void BuildUi()
    {
        toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), WrapContents = true };
        var import = new Button { Text = "导入 TXT", AutoSize = true };
        import.Click += ImportClick;
        var addBookmark = new Button { Text = "加书签", AutoSize = true };
        addBookmark.Click += AddBookmark;
        var removeBookmark = new Button { Text = "删书签", AutoSize = true };
        removeBookmark.Click += RemoveBookmark;
        hideButton.Click += (_, _) => HideToTray();
        directoryButton.Click += (_, _) => ToggleDirectoryDrawer();
        var focusButton = new Button { Text = "专注模式", AutoSize = true };
        focusButton.Click += (_, _) => ToggleFocusMode();
        var settingsButton = new Button { Text = "设置", AutoSize = true };
        settingsButton.Click += (_, _) => OpenSettings();
        var fontButton = new Button { Text = "字体", AutoSize = true };
        fontButton.Click += (_, _) =>
        {
            using var dialog = new FontDialog { Font = reader.Font, MinSize = 1, MaxSize = 32, ShowEffects = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            int offset = GetVisibleOffset();
            settings.FontFamilyName = dialog.Font.Name;
            settings.FontSize = Math.Clamp(dialog.Font.Size, 1, 32);
            fontSize.Value = (decimal)settings.FontSize;
            ApplyFont(); ScrollToOffset(offset); SaveState();
        };
        mode.Items.AddRange(["滚动", "翻页"]); mode.SelectedItem = settings.Mode;
        mode.SelectedIndexChanged += (_, _) => { settings.Mode = mode.Text; ApplyMode(); SaveState(); };
        theme.Items.AddRange(["浅色", "夜间"]); theme.SelectedItem = settings.Theme;
        theme.SelectedIndexChanged += (_, _) => { settings.Theme = theme.Text; ApplyTheme(); SaveState(); };
        fontSize.Value = (decimal)Math.Clamp(settings.FontSize, 1, 32);
        fontSize.ValueChanged += (_, _) => { int offset = GetVisibleOffset(); settings.FontSize = (float)fontSize.Value; ApplyFont(); ScrollToOffset(offset); SaveState(); };
        searchBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) StartSearch(); };
        var search = new Button { Text = "搜索", AutoSize = true }; search.Click += (_, _) => StartSearch();
        toolbar.Controls.AddRange([import, addBookmark, removeBookmark, directoryButton, focusButton, settingsButton, hideButton, fontButton, new Label { Text = "模式", AutoSize = true, Padding = new Padding(12, 7, 0, 0) }, mode, new Label { Text = "主题", AutoSize = true, Padding = new Padding(12, 7, 0, 0) }, theme, new Label { Text = "字号", AutoSize = true, Padding = new Padding(12, 7, 0, 0) }, fontSize, searchBox, search]);
        Controls.Add(toolbar);

        var left = sidebarTabs;
        var bookTab = new TabPage("书架"); bookTab.Controls.Add(books); books.SelectedIndexChanged += (_, _) => SelectBook(); left.TabPages.Add(bookTab);
        var chapterTab = new TabPage("目录"); chapterTab.Controls.Add(chapters); chapters.SelectedIndexChanged += (_, _) => SelectChapter(); left.TabPages.Add(chapterTab);
        var searchTab = new TabPage("搜索结果"); searchTab.Controls.Add(results); results.DoubleClick += (_, _) => JumpSearch(); left.TabPages.Add(searchTab);
        var bookmarkTab = new TabPage("书签"); bookmarkTab.Controls.Add(bookmarks); bookmarks.DoubleClick += (_, _) => JumpBookmark(); left.TabPages.Add(bookmarkTab);

        var split = mainSplit;
        split.Panel1.Controls.Add(left);
        readerTitle = new Label { Dock = DockStyle.Top, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        readerPanel.Padding = new Padding(Math.Clamp(settings.TextMargin, 0, 100));
        readerPanel.Controls.Add(reader);
        split.Panel2.Controls.Add(readerPanel); split.Panel2.Controls.Add(readerTitle);
        reader.KeyDown += ReaderKeyDown; reader.MouseUp += (_, _) => SavePosition(); reader.VScroll += (_, _) => SavePosition();
        reader.MouseDown += (_, e) => { if (settings.Mode == "翻页" && e.Button == MouseButtons.Left) PageClick(e.X); };
        Controls.Add(split);
        ApplyTheme(); ApplyFont();
    }

    private void LoadBooks()
    {
        books.Items.Clear(); books.Items.AddRange(settings.Books.ToArray());
        if (settings.CurrentBookId != null)
        {
            int index = settings.Books.FindIndex(x => x.Id == settings.CurrentBookId);
            if (index >= 0) books.SelectedIndex = index;
        }
        if (books.SelectedIndex < 0 && books.Items.Count > 0) books.SelectedIndex = 0;
        if (books.SelectedIndex >= 0 && sidebarTabs.TabPages.Count > 1) { sidebarTabs.SelectedIndex = 1; mainSplit.Panel1Collapsed = false; directoryButton.Text = "收起目录"; }
    }

    private void SelectBook()
    {
        if (books.SelectedItem is not BookRecord book) return;
        SavePosition();
        searchCancellation?.Cancel();
        results.Items.Clear();
        currentBook = book; settings.CurrentBookId = book.Id;
        currentChapter = -1;
        loading = true;
        chapters.Items.Clear(); chapters.Items.AddRange(book.Chapters.ToArray());
        bookmarks.Items.Clear(); bookmarks.Items.AddRange(book.Bookmarks.ToArray());
        int index = Math.Clamp(book.LastChapter, 0, Math.Max(0, book.Chapters.Count - 1));
        if (book.Chapters.Count > 0) chapters.SelectedIndex = index;
        loading = false;
        if (book.Chapters.Count > 0) OpenChapter(index, book.LastOffset);
    }

    private void SelectChapter()
    {
        if (loading || currentBook == null || chapters.SelectedIndex < 0) return;
        OpenChapter(chapters.SelectedIndex, 0);
    }

    private void OpenChapter(int index, int offset)
    {
        if (currentBook == null || index < 0 || index >= currentBook.Chapters.Count) return;
        loading = true;
        try
        {
            string content = LibraryStore.ReadChapter(currentBook, index);
            currentChapter = index; currentBook.LastChapter = index;
            reader.Text = content;
            ScrollToOffset(offset);
            currentBook.LastOffset = Math.Clamp(offset, 0, reader.TextLength);
            chapters.SelectedIndex = index;
            if (readerTitle != null) readerTitle.Text = $"{currentBook.Title}  ·  {currentBook.Chapters[index].Title}  ({index + 1}/{currentBook.Chapters.Count})";
            ApplyMode();
            loading = false;
            Persist();
        }
        catch (Exception ex) { loading = false; MessageBox.Show(ex.Message, "打开章节失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async void ImportClick(object? sender, EventArgs e)
    {
        if (importing) return;
        using var dialog = new OpenFileDialog { Filter = "TXT 文件|*.txt|所有文件|*.*", Title = "选择小说 TXT" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            importing = true;
            if (sender is Button button) button.Enabled = false;
            Cursor = Cursors.WaitCursor;
            var progress = new Progress<string>(text => { if (!closing) Text = "Read_me · " + text; });
            var book = await Task.Run(() => TxtImporter.Import(dialog.FileName, LibraryStore.Root, progress));
            var duplicate = settings.Books.FirstOrDefault(x => x.Fingerprint == book.Fingerprint);
            if (duplicate != null)
            {
                Directory.Delete(Path.Combine(LibraryStore.Root, book.Id), true);
                books.SelectedItem = duplicate;
                MessageBox.Show(this, "这本书已经在书架中，已打开现有书籍。", "重复导入");
                return;
            }
            settings.Books.Add(book); settings.CurrentBookId = book.Id; SaveState(); LoadBooks();
            books.SelectedItem = book;
            if (mainSplit.Panel1Collapsed) mainSplit.Panel1Collapsed = false;
            if (sidebarTabs.TabPages.Count > 1) sidebarTabs.SelectedIndex = 1;
            directoryButton.Text = "收起目录";
            MessageBox.Show($"导入完成：{book.Chapters.Count} 个章节", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { importing = false; Cursor = Cursors.Default; Text = "Read_me"; if (sender is Button button) button.Enabled = true; }
    }

    private async void StartSearch()
    {
        if (currentBook == null) return;
        string query = searchBox.Text.Trim(); if (query.Length == 0) return;
        searchCancellation?.Cancel(); searchCancellation = new CancellationTokenSource();
        results.Items.Clear(); results.Items.Add("搜索中…");
        var token = searchCancellation.Token; var book = currentBook;
        try
        {
            var hits = await Task.Run(() => BookSearch.Search(book, query, 301, token), token);
            if (closing || IsDisposed || token.IsCancellationRequested || currentBook?.Id != book.Id) return;
            results.Items.Clear();
            results.Items.AddRange(hits.Take(300).ToArray());
            if (hits.Count == 0) results.Items.Add("没有找到匹配内容");
            if (hits.Count > 300) results.Items.Add("仅显示前 300 条结果，请缩小关键词范围");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing && !IsDisposed && !token.IsCancellationRequested && currentBook?.Id == book.Id) { results.Items.Clear(); results.Items.Add("搜索失败：" + ex.Message); } }
    }

    private void JumpSearch()
    {
        if (results.SelectedItem is SearchHit hit && hit.BookId == currentBook?.Id)
        {
            OpenChapter(hit.Chapter, hit.Offset);
            reader.Select(Math.Clamp(hit.Offset, 0, reader.TextLength), Math.Min(searchBox.Text.Trim().Length, Math.Max(0, reader.TextLength - hit.Offset)));
            reader.ScrollToCaret();
        }
    }

    private void RestoreReadingPosition()
    {
        if (currentBook == null || currentChapter < 0) return;
        loading = true;
        ScrollToOffset(currentBook.LastOffset);
        loading = false;
    }

    private void ScrollToOffset(int offset)
    {
        reader.Select(Math.Clamp(offset, 0, reader.TextLength), 0);
        reader.ScrollToCaret();
        if (reader.IsHandleCreated)
        {
            int line = reader.GetLineFromCharIndex(reader.SelectionStart);
            SendMessage(reader.Handle, 0x00B6, IntPtr.Zero, (IntPtr)(line - FirstVisibleLine()));
        }
    }

    private void RestoreWindow()
    {
        if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
        {
            var bounds = new Rectangle(settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds))) { StartPosition = FormStartPosition.Manual; Bounds = bounds; }
        }
        mainSplit.Panel1MinSize = 0;
        mainSplit.Panel2MinSize = 0;
        mainSplit.SplitterDistance = Math.Clamp(settings.SidebarWidth, 0, Math.Max(0, mainSplit.Width - mainSplit.SplitterWidth));
        if (settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }
    private void AddBookmark(object? sender, EventArgs e)
    {
        if (currentBook == null || currentChapter < 0) return;
        int offset = reader.SelectionLength > 0 ? reader.SelectionStart : GetVisibleOffset(); var bookmark = new BookmarkRecord { Chapter = currentChapter, Offset = offset, Label = $"{currentBook.Chapters[currentChapter].Title} · 位置 {offset}" };
        currentBook.Bookmarks.Add(bookmark); bookmarks.Items.Add(bookmark); SaveState();
    }
    private void RemoveBookmark(object? sender, EventArgs e) { if (bookmarks.SelectedItem is BookmarkRecord item && currentBook != null) { currentBook.Bookmarks.Remove(item); bookmarks.Items.Remove(item); SaveState(); } }
    private void JumpBookmark() { if (bookmarks.SelectedItem is BookmarkRecord item) OpenChapter(item.Chapter, item.Offset); }

    private void ToggleDirectoryDrawer()
    {
        if (focusMode)
        {
            bool wasCollapsed = sidebarWasCollapsed;
            ToggleFocusMode();
            if (wasCollapsed) mainSplit.Panel1Collapsed = false;
            directoryButton.Text = "收起目录";
            sidebarTabs.SelectedIndex = 1;
            return;
        }
        mainSplit.Panel1Collapsed = !mainSplit.Panel1Collapsed;
        if (mainSplit.Panel1Collapsed) directoryButton.Text = "目录";
        else
        {
            directoryButton.Text = "收起目录";
            sidebarTabs.SelectedIndex = 1;
        }
    }

    private void ToggleFocusMode()
    {
        if (toolbar == null || readerTitle == null) return;
        if (!focusMode)
        {
            SaveState();
            focusMode = true;
            previousBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            previousWindowState = WindowState;
            previousBorderStyle = FormBorderStyle;
            sidebarWasCollapsed = mainSplit.Panel1Collapsed;
            mainSplit.Panel1Collapsed = true;
            toolbar.Visible = false;
            readerTitle.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            if (previousWindowState == FormWindowState.Normal) Bounds = previousBounds;
            reader.Focus();
        }
        else
        {
            focusMode = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = previousBorderStyle;
            Bounds = previousBounds;
            toolbar.Visible = true;
            readerTitle.Visible = true;
            mainSplit.Panel1Collapsed = sidebarWasCollapsed;
            directoryButton.Text = sidebarWasCollapsed ? "目录" : "收起目录";
            if (previousWindowState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
            reader.Focus();
        }
    }

    private void OpenSettings()
    {
        using var dialog = new ReaderSettingsDialog(settings);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            settings.FocusModeHotkey = (int)dialog.FocusModeKey;
            settings.HideWindowHotkey = (int)dialog.HideWindowKey;
            settings.DirectoryHotkey = (int)dialog.DirectoryKey;
            int offset = GetVisibleOffset();
            settings.TextMargin = dialog.TextMargin;
            readerPanel.Padding = new Padding(settings.TextMargin);
            ScrollToOffset(offset);
            SaveState();
        }
    }

    private void ReaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Right) { NextChapter(); e.SuppressKeyPress = true; return; }
        if (e.Control && e.KeyCode == Keys.Left) { PreviousChapter(); e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.Up) { ScrollLines(-3); e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.Down) { ScrollLines(3); e.SuppressKeyPress = true; return; }
        if (e.KeyCode is Keys.Left or Keys.PageUp) { ScrollLines(-VisibleLineCount()); e.SuppressKeyPress = true; return; }
        if (e.KeyCode is Keys.Right or Keys.PageDown) { ScrollLines(VisibleLineCount()); e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.Home) { reader.Select(0, 0); reader.ScrollToCaret(); SavePosition(); e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.End) { reader.Select(reader.TextLength, 0); reader.ScrollToCaret(); SavePosition(); e.SuppressKeyPress = true; }
    }

    private int VisibleLineCount()
    {
        if (reader.ClientSize.Height < 4) return 1;
        int topChar = reader.GetCharIndexFromPosition(new Point(2, 2));
        int bottomChar = reader.GetCharIndexFromPosition(new Point(2, reader.ClientSize.Height - 2));
        int topLine = Math.Max(0, reader.GetLineFromCharIndex(Math.Max(0, topChar)));
        int bottomLine = Math.Max(topLine + 1, reader.GetLineFromCharIndex(Math.Max(0, bottomChar)));
        return Math.Max(1, bottomLine - topLine);
    }

    private int FirstVisibleLine() => reader.IsHandleCreated
        ? SendMessage(reader.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32()
        : 0;

    private int DisplayLineCount() => reader.IsHandleCreated
        ? Math.Max(1, SendMessage(reader.Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32())
        : 1;

    private void ScrollLines(int count)
    {
        if (count == 0 || currentBook == null || currentChapter < 0) return;
        int first = FirstVisibleLine();
        int visible = VisibleLineCount();
        int lines = DisplayLineCount();
        if (count < 0 && first <= 0 && currentChapter > 0)
        {
            OpenChapter(currentChapter - 1, int.MaxValue);
            return;
        }
        SendMessage(reader.Handle, 0x00B6, IntPtr.Zero, (IntPtr)count);
        int after = FirstVisibleLine();
        if (count > 0 && after == first && first + visible >= lines - 1 && currentChapter + 1 < currentBook.Chapters.Count)
        {
            OpenChapter(currentChapter + 1, 0);
            return;
        }
        SavePosition();
    }

    private void PageClick(int x) => ScrollLines(x < reader.ClientSize.Width / 2 ? -VisibleLineCount() : VisibleLineCount());

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void NextChapter() { if (currentBook != null && currentChapter + 1 < currentBook.Chapters.Count) OpenChapter(currentChapter + 1, 0); }
    private void PreviousChapter() { if (currentBook != null && currentChapter > 0) OpenChapter(currentChapter - 1, 0); }
    private int GetVisibleOffset()
    {
        if (reader.TextLength == 0 || !reader.IsHandleCreated) return reader.SelectionStart;
        return Math.Clamp(reader.GetCharIndexFromPosition(new Point(2, 2)), 0, reader.TextLength);
    }

    private void SavePosition()
    {
        if (!loading && currentBook != null && currentChapter >= 0)
        {
            currentBook.LastChapter = currentChapter;
            currentBook.LastOffset = GetVisibleOffset();
            saveTimer.Stop();
            saveTimer.Start();
        }
    }

    private void SaveState()
    {
        if (loading) return;
        if (currentBook != null && currentChapter >= 0)
        {
            currentBook.LastChapter = currentChapter;
            currentBook.LastOffset = GetVisibleOffset();
        }
        Rectangle bounds = focusMode ? previousBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.WindowX = bounds.X; settings.WindowY = bounds.Y;
        settings.WindowWidth = bounds.Width; settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = (focusMode ? previousWindowState : WindowState) == FormWindowState.Maximized;
        settings.SidebarWidth = mainSplit.SplitterDistance;
        Persist();
    }

    private void Persist()
    {
        saveTimer.Stop();
        try { LibraryStore.Save(settings); saveFailed = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!saveFailed) MessageBox.Show(this, "存档保存失败，请检查磁盘空间和 ReaderData 文件夹权限。\n" + ex.Message, "存档失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            saveFailed = true;
        }
    }
    private void ApplyFont()
    {
        if (Font.Name != "Microsoft YaHei UI") Font = new Font("Microsoft YaHei UI", 9F);
        var next = new Font(string.IsNullOrWhiteSpace(settings.FontFamilyName) ? "Microsoft YaHei UI" : settings.FontFamilyName, Math.Clamp(settings.FontSize, 1, 32), FontStyle.Regular);
        reader.Font = next;
        readingFont?.Dispose();
        readingFont = next;
    }
    private void ApplyTheme()
    {
        bool night = settings.Theme == "夜间";
        BackColor = night ? Color.FromArgb(32, 34, 38) : Color.White;
        reader.BackColor = night ? Color.FromArgb(42, 44, 48) : Color.White;
        reader.ForeColor = night ? Color.Gainsboro : Color.FromArgb(35, 35, 35);
        foreach (Control child in Controls) ApplyThemeToControls(child, night);
    }

    private static void ApplyThemeToControls(Control control, bool night)
    {
        if (control is not ToolStrip && control is not NumericUpDown)
        {
            control.BackColor = night ? Color.FromArgb(45, 47, 52) : Color.White;
            control.ForeColor = night ? Color.Gainsboro : Color.FromArgb(35, 35, 35);
        }
        foreach (Control child in control.Controls) ApplyThemeToControls(child, night);
    }

    private void HideToTray()
    {
        Hide();
        trayIcon.ShowBalloonTip(1200, "Read_me", "窗口已隐藏，双击托盘图标可恢复。", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }
    private void ApplyMode() { reader.ReadOnly = true; reader.ScrollBars = settings.Mode == "滚动" ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None; }
}


internal sealed class ReaderSettingsDialog : Form
{
    private readonly HotkeyTextBox focusKey;
    private readonly HotkeyTextBox hideKey;
    private readonly HotkeyTextBox directoryKey;
    private readonly NumericUpDown textMargin;
    public int TextMargin => (int)textMargin.Value;
    public Keys FocusModeKey => focusKey.Hotkey;
    public Keys HideWindowKey => hideKey.Hotkey;
    public Keys DirectoryKey => directoryKey.Hotkey;

    public ReaderSettingsDialog(ReaderSettings settings)
    {
        Text = "Read_me 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;
        ClientSize = new Size(430, 320);
        Font = new Font("Microsoft YaHei UI", 9F);

        focusKey = new HotkeyTextBox((Keys)settings.FocusModeHotkey);
        hideKey = new HotkeyTextBox((Keys)settings.HideWindowHotkey);
        directoryKey = new HotkeyTextBox((Keys)settings.DirectoryHotkey);
        textMargin = new NumericUpDown { Minimum = 0, Maximum = 100, Value = Math.Clamp(settings.TextMargin, 0, 100), Dock = DockStyle.Fill };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 190, Padding = new Padding(14), ColumnCount = 2, RowCount = 4 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        for (int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        grid.Controls.Add(new Label { Text = "专注模式", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        grid.Controls.Add(focusKey, 1, 0);
        grid.Controls.Add(new Label { Text = "隐藏窗口到托盘", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        grid.Controls.Add(hideKey, 1, 1);
        grid.Controls.Add(new Label { Text = "展开／收起目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        grid.Controls.Add(directoryKey, 1, 2);
        grid.Controls.Add(new Label { Text = "正文四周留白（像素）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        grid.Controls.Add(textMargin, 1, 3);
        var note = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(16, 4, 8, 0), Text = "点击输入框后按下要设置的组合键。Esc 可退出专注模式。" };
        var error = new Label { Dock = DockStyle.Bottom, Height = 26, ForeColor = Color.Firebrick, Padding = new Padding(16, 3, 0, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var save = new Button { Text = "保存", DialogResult = DialogResult.None, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) =>
        {
            var values = new[] { focusKey.Hotkey, hideKey.Hotkey, directoryKey.Hotkey };
            if (values.Any(k => k == Keys.None)) { error.Text = "请为每个操作设置快捷键。"; return; }
            if (values.Distinct().Count() != values.Length) { error.Text = "快捷键不能重复。"; return; }
            DialogResult = DialogResult.OK; Close();
        };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        Controls.Add(grid); Controls.Add(note); Controls.Add(error); Controls.Add(buttons);
        AcceptButton = save; CancelButton = cancel;
    }
}

internal sealed class HotkeyTextBox : TextBox
{
    public Keys Hotkey { get; private set; }

    public HotkeyTextBox(Keys hotkey)
    {
        Hotkey = hotkey;
        ReadOnly = true;
        TextAlign = HorizontalAlignment.Center;
        Dock = DockStyle.Fill;
        Text = Format(hotkey);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        Hotkey = e.KeyCode | e.Modifiers;
        Text = Format(Hotkey);
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    private static string Format(Keys keys)
    {
        if (keys == Keys.None) return "按下快捷键";
        var parts = new List<string>();
        if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
        if ((keys & Keys.Alt) != 0) parts.Add("Alt");
        if ((keys & Keys.Shift) != 0) parts.Add("Shift");
        Keys code = keys & Keys.KeyCode;
        if (code != Keys.None) parts.Add(code.ToString());
        return string.Join("+", parts);
    }
}
