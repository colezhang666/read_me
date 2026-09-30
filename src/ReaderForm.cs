using System.Drawing;
using System.Text;

namespace PocketReader;

public sealed class ReaderForm : Form
{
    private readonly ReaderSettings settings;
    private readonly TabControl sidebarTabs = new() { Dock = DockStyle.Fill, Width = 285 };
    private readonly SplitContainer mainSplit = new() { Dock = DockStyle.Fill, SplitterDistance = 285, FixedPanel = FixedPanel.Panel1 };
    private ToolStrip? toolbar;
    private bool focusMode;
    private bool sidebarWasCollapsed;
    private Rectangle previousBounds;
    private FormWindowState previousWindowState;
    private FormBorderStyle previousBorderStyle;
    private Label? readerTitle;
    private readonly Button directoryButton = new() { Text = "收起目录", AutoSize = true };
    private readonly ListBox books = new() { Dock = DockStyle.Fill };
    private readonly ListBox removedBooks = new() { Dock = DockStyle.Fill };
    private readonly ListBox chapters = new() { Dock = DockStyle.Fill };
    private readonly ReadingBox reader = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DetectUrls = false, ReadOnly = true, HideSelection = false };
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
    private readonly TextBox chapterFilter = new() { Width = 160, PlaceholderText = "筛选章节名称" };
    private readonly TextBox chapterNumber = new() { Width = 65, PlaceholderText = "章号" };
    private readonly Button goChapterButton = new() { Text = "跳转", AutoSize = true };
    private (string BookId, int Chapter, int Offset)? searchOrigin;
    private string chapterContent = "";
    private List<int> pageStarts = [0];
    private int pageIndex;
    private bool reflowPending;

    public ReaderForm()
    {
        Text = "Read_me";
        Width = 1260; Height = 820; MinimumSize = Size.Empty;
        StartPosition = FormStartPosition.CenterScreen;
        settings = LibraryStore.Load();
        if (string.IsNullOrWhiteSpace(settings.Theme)) settings.Theme = "浅色";
        KeyPreview = true;
        KeyDown += HandleShortcut;
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
            ApplyParagraphLayout();
            RestoreReadingPosition(); Reflow();
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
        toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, CanOverflow = true, AutoSize = false, Height = 32 };
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
            ApplyFont(); Reflow(offset); SaveState();
        };
        mode.Items.AddRange(["滚动", "翻页"]); mode.SelectedItem = settings.Mode;
        mode.SelectedIndexChanged += (_, _) => { int offset = reader.Paged ? pageStarts[pageIndex] : GetVisibleOffset(); settings.Mode = mode.Text; Reflow(offset); SaveState(); };
        theme.Items.AddRange(["浅色", "夜间"]); theme.SelectedItem = settings.Theme;
        theme.SelectedIndexChanged += (_, _) => { settings.Theme = theme.Text; ApplyTheme(); SaveState(); };
        fontSize.Value = (decimal)Math.Clamp(settings.FontSize, 1, 32);
        fontSize.ValueChanged += (_, _) => { int offset = GetVisibleOffset(); settings.FontSize = (float)fontSize.Value; ApplyFont(); Reflow(offset); SaveState(); };
        searchBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) StartSearch(); };
        var search = new Button { Text = "搜索", AutoSize = true }; search.Click += (_, _) => { OpenSearch(); if (searchBox.Text.Trim().Length > 0) StartSearch(); };
        Control[] toolbarControls = [import, addBookmark, removeBookmark, directoryButton, focusButton, settingsButton, hideButton, fontButton, new Label { Text = "模式", AutoSize = true, Padding = new Padding(12, 7, 0, 0) }, mode, new Label { Text = "主题", AutoSize = true, Padding = new Padding(12, 7, 0, 0) }, theme, new Label { Text = "字号", AutoSize = true, Padding = new Padding(12, 7, 0, 0) }, fontSize, search];
        foreach (var control in toolbarControls)
        {
            if (control is Button button) { var item = new ToolStripButton(button.Text); item.Click += (_, _) => button.PerformClick(); button.TextChanged += (_, _) => item.Text = button.Text; button.EnabledChanged += (_, _) => item.Enabled = button.Enabled; toolbar.Items.Add(item); }
            else if (control is Label label) toolbar.Items.Add(new ToolStripLabel(label.Text));
            else toolbar.Items.Add(new ToolStripControlHost(control) { AutoSize = false, Size = new Size(control.Width, 25) });
        }
        Controls.Add(toolbar);

        var left = sidebarTabs;
        var bookTab = new TabPage("书架"); bookTab.Controls.Add(books); books.SelectedIndexChanged += (_, _) => SelectBook(); left.TabPages.Add(bookTab);
        var bookMenu = new ContextMenuStrip();
        bookMenu.Items.Add("重命名", null, (_, _) => RenameSelectedBook());
        bookMenu.Items.Add("移入回收站", null, (_, _) =>
        {
            if (books.SelectedItem is BookRecord book && MessageBox.Show(this, $"将《{book.Title}》移入回收站？正文、进度和书签会保留，可恢复。", "移除书籍", MessageBoxButtons.YesNo) == DialogResult.Yes) RemoveBook(book);
        });
        books.ContextMenuStrip = bookMenu;
        books.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) { int index = books.IndexFromPoint(e.Location); if (index >= 0) books.SelectedIndex = index; } };
        var chapterTab = new TabPage("目录"); chapterTab.Controls.Add(chapters);
        var directoryControls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        directoryControls.Controls.AddRange([chapterFilter, chapterNumber, goChapterButton]);
        chapterTab.Controls.Add(directoryControls);
        chapterFilter.TextChanged += (_, _) => RefreshChapters();
        goChapterButton.Click += (_, _) => GoChapterNumber();
        chapterNumber.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { GoChapterNumber(); e.SuppressKeyPress = true; } }; chapters.SelectedIndexChanged += (_, _) => SelectChapter(); left.TabPages.Add(chapterTab);
        var searchTab = new TabPage("搜索结果"); searchTab.Controls.Add(results);
        var searchControls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        searchControls.Controls.Add(searchBox);
        var runSearch = new Button { Text = "搜索", AutoSize = true }; runSearch.Click += (_, _) => StartSearch(); searchControls.Controls.Add(runSearch);
        foreach (var item in new[] { ("上一个", -1), ("下一个", 1), ("返回阅读", 0) }) { var button = new Button { Text = item.Item1, AutoSize = true }; button.Click += (_, _) => { if (item.Item2 == 0) ReturnFromSearch(); else MoveSearchResult(item.Item2); }; searchControls.Controls.Add(button); }
        searchTab.Controls.Add(searchControls); results.DoubleClick += (_, _) => JumpSearch(); left.TabPages.Add(searchTab);
        var bookmarkTab = new TabPage("书签"); bookmarkTab.Controls.Add(bookmarks); bookmarks.DoubleClick += (_, _) => JumpBookmark(); left.TabPages.Add(bookmarkTab);
        var trashTab = new TabPage("回收站"); trashTab.Controls.Add(removedBooks); left.TabPages.Add(trashTab);
        var trashMenu = new ContextMenuStrip();
        trashMenu.Items.Add("恢复到书架", null, (_, _) => { if (removedBooks.SelectedItem is BookRecord book) RestoreBook(book); });
        removedBooks.ContextMenuStrip = trashMenu;
        removedBooks.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) { int index = removedBooks.IndexFromPoint(e.Location); if (index >= 0) removedBooks.SelectedIndex = index; } };
        removedBooks.DoubleClick += (_, _) => { if (removedBooks.SelectedItem is BookRecord book) RestoreBook(book); };

        var split = mainSplit;
        split.Panel1.Controls.Add(left);
        readerTitle = new Label { Dock = DockStyle.Top, Height = 34, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0), Font = new Font(Font, FontStyle.Bold) };
        readerPanel.Padding = new Padding(Math.Clamp(settings.TextMargin, 0, 100));
        readerPanel.Controls.Add(reader);
        split.Panel2.Controls.Add(readerPanel); split.Panel2.Controls.Add(readerTitle);
        reader.MouseUp += (_, _) => SavePosition(); reader.VScroll += (_, _) => SavePosition();
        reader.MouseDown += (_, e) => { if (settings.Mode == "翻页" && e.Button == MouseButtons.Left) PageClick(e.X); };
        Controls.Add(split);
        ApplyTheme(); ApplyFont();
        reader.TurnPage = TurnPage; reader.DragWindow = _ => BeginWindowAction(2);
        reader.EdgeHit = ResizeHit; reader.ResizeWindow = BeginWindowAction;
        reader.SizeChanged += (_, _) => { if (!loading && currentBook != null) ScheduleReflow(); };
        readerPanel.MouseDown += (_, e) => { if (focusMode && e.Button == MouseButtons.Left) { int hit = ResizeHit(readerPanel.PointToScreen(e.Location)); if (hit != 0) BeginWindowAction(hit); } };
        readerPanel.MouseMove += (_, e) => { int hit = focusMode ? ResizeHit(readerPanel.PointToScreen(e.Location)) : 0; readerPanel.Cursor = hit is 10 or 11 ? Cursors.SizeWE : hit is 12 or 15 ? Cursors.SizeNS : hit is 13 or 17 ? Cursors.SizeNWSE : hit is 14 or 16 ? Cursors.SizeNESW : Cursors.Default; };
    }

    private void LoadBooks()
    {
        books.Items.Clear(); books.Items.AddRange(settings.Books.ToArray());
        removedBooks.Items.Clear(); removedBooks.Items.AddRange(settings.RemovedBooks.ToArray());
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
        searchOrigin = null;
        results.Items.Clear();
        currentBook = book; settings.CurrentBookId = book.Id;
        currentChapter = -1;
        loading = true;
        chapterFilter.Clear(); RefreshChapters();
        bookmarks.Items.Clear(); bookmarks.Items.AddRange(book.Bookmarks.ToArray());
        int index = Math.Clamp(book.LastChapter, 0, Math.Max(0, book.Chapters.Count - 1));
        if (book.Chapters.Count > 0) chapters.SelectedItem = book.Chapters[index];
        loading = false;
        if (book.Chapters.Count > 0) OpenChapter(index, book.LastOffset);
    }

    private void SelectChapter()
    {
        if (loading || currentBook == null || chapters.SelectedIndex < 0) return;
        OpenChapter(currentBook.Chapters.IndexOf((ChapterRecord)chapters.SelectedItem!), 0);
    }

    private void OpenChapter(int index, int offset)
    {
        if (currentBook == null || index < 0 || index >= currentBook.Chapters.Count) return;
        loading = true;
        try
        {
            string content = LibraryStore.ReadChapter(currentBook, index);
            currentChapter = index; currentBook.LastChapter = index;
            reader.Text = content; chapterContent = reader.Text; pageStarts = [0]; pageIndex = 0;
            ApplyParagraphLayout();
            ScrollToOffset(offset);
            currentBook.LastOffset = Math.Clamp(offset, 0, chapterContent.Length);
            chapters.SelectedItem = currentBook.Chapters[index];
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
        using var preview = new ImportPreviewDialog(dialog.FileName);
        if (preview.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            importing = true;
            if (sender is Button button) button.Enabled = false;
            Cursor = Cursors.WaitCursor;
            var progress = new Progress<string>(text => { if (!closing) Text = "Read_me · " + text; });
            var book = await Task.Run(() => TxtImporter.Import(dialog.FileName, LibraryStore.Root, progress, options: preview.Options));
            var duplicate = settings.Books.Concat(settings.RemovedBooks).FirstOrDefault(x => x.Fingerprint == book.Fingerprint);
            if (duplicate != null)
            {
                Directory.Delete(Path.Combine(LibraryStore.Root, book.Id), true);
                if (settings.RemovedBooks.Contains(duplicate)) RestoreBook(duplicate);
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
            if (searchOrigin == null) searchOrigin = (currentBook!.Id, currentChapter, GetVisibleOffset());
            OpenChapter(hit.Chapter, hit.Offset);
            int localOffset = hit.Offset - (settings.Mode == "翻页" ? pageStarts[pageIndex] : 0);
            reader.Select(Math.Clamp(localOffset, 0, reader.TextLength), Math.Min(searchBox.Text.Trim().Length, Math.Max(0, reader.TextLength - localOffset)));
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
        if (reader.Paged) offset -= pageStarts[pageIndex];
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
        int offset = reader.SelectionLength > 0 ? reader.SelectionStart + (settings.Mode == "翻页" ? pageStarts[pageIndex] : 0) : GetVisibleOffset(); var bookmark = new BookmarkRecord { Chapter = currentChapter, Offset = offset, Label = $"{currentBook.Chapters[currentChapter].Title} · 位置 {offset}" };
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
            Rectangle focusBounds = Bounds;
            focusMode = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = previousBorderStyle;
            Bounds = previousWindowState == FormWindowState.Normal ? focusBounds : previousBounds;
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
            settings.NavigationHotkeys = dialog.NavigationHotkeys;
            int offset = GetVisibleOffset();
            settings.TextMargin = dialog.TextMargin;
            settings.LineSpacing = dialog.LineSpacing;
            settings.ParagraphSpacing = dialog.ParagraphSpacing;
            readerPanel.Padding = new Padding(settings.TextMargin);
            ApplyParagraphLayout();
            Reflow(offset);
            SaveState();
        }
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
        if (settings.Mode == "翻页") { TurnPage(Math.Sign(count)); return; }
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

    private void PageClick(int x) => TurnPage(x < reader.ClientSize.Width / 2 ? -1 : 1);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void NextChapter() { if (currentBook != null && currentChapter + 1 < currentBook.Chapters.Count) OpenChapter(currentChapter + 1, 0); }
    private void PreviousChapter() { if (currentBook != null && currentChapter > 0) OpenChapter(currentChapter - 1, 0); }
    private int GetVisibleOffset()
    {
        if (settings.Mode == "翻页") return pageStarts[Math.Clamp(pageIndex, 0, pageStarts.Count - 1)];
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
        Rectangle bounds = focusMode && previousWindowState != FormWindowState.Normal ? previousBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.WindowX = bounds.X; settings.WindowY = bounds.Y;
        settings.WindowWidth = bounds.Width; settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = (focusMode ? previousWindowState : WindowState) == FormWindowState.Maximized;
        settings.SidebarWidth = mainSplit.SplitterDistance;
        Persist();
    }

    private bool Persist()
    {
        saveTimer.Stop();
        try { LibraryStore.Save(settings); saveFailed = false; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!saveFailed) MessageBox.Show(this, "存档保存失败，请检查磁盘空间和 ReaderData 文件夹权限。\n" + ex.Message, "存档失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            saveFailed = true;
            return false;
        }
    }

    private void RenameSelectedBook()
    {
        if (books.SelectedItem is not BookRecord book) return;
        using var dialog = new Form { Text = "重命名书籍", ClientSize = new Size(380, 105), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var input = new TextBox { Text = book.Title, Left = 12, Top = 15, Width = 355, MaxLength = 200 };
        var save = new Button { Text = "保存", Left = 285, Top = 60, DialogResult = DialogResult.OK };
        dialog.Controls.AddRange([input, save]); dialog.AcceptButton = save;
        if (dialog.ShowDialog(this) == DialogResult.OK && !RenameBook(book, input.Text)) MessageBox.Show(this, "书名不能为空且不能超过200个字符，或存档保存失败。", "重命名失败");
    }

    private bool RenameBook(BookRecord book, string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title.Length > 200 || !settings.Books.Contains(book)) return false;
        string old = book.Title; book.Title = title;
        if (!Persist()) { book.Title = old; return false; }
        int index = books.Items.IndexOf(book); if (index >= 0) books.Items[index] = book;
        if (currentBook == book && currentChapter >= 0 && readerTitle != null) readerTitle.Text = $"{book.Title}  ·  {book.Chapters[currentChapter].Title}  ({currentChapter + 1}/{book.Chapters.Count})";
        return true;
    }

    private void RemoveBook(BookRecord book)
    {
        if (!settings.Books.Contains(book)) return;
        SaveState(); if (saveFailed) return;
        int index = settings.Books.IndexOf(book);
        string? previousId = settings.CurrentBookId;
        settings.Books.Remove(book); settings.RemovedBooks.Add(book);
        if (previousId == book.Id) settings.CurrentBookId = settings.Books.FirstOrDefault()?.Id;
        if (!Persist()) { settings.RemovedBooks.Remove(book); settings.Books.Insert(index, book); settings.CurrentBookId = previousId; return; }
        ClearReading(); LoadBooks();
    }

    private void RestoreBook(BookRecord book)
    {
        if (!settings.RemovedBooks.Contains(book)) return;
        SaveState(); if (saveFailed) return;
        string? previousId = settings.CurrentBookId;
        int index = settings.RemovedBooks.IndexOf(book);
        settings.RemovedBooks.Remove(book); settings.Books.Add(book); settings.CurrentBookId = book.Id;
        if (!Persist()) { settings.Books.Remove(book); settings.RemovedBooks.Insert(index, book); settings.CurrentBookId = previousId; return; }
        ClearReading(); LoadBooks();
    }

    private void ClearReading()
    {
        searchCancellation?.Cancel();
        loading = true;
        currentBook = null; currentChapter = -1;
        chapterContent = ""; pageStarts = [0]; pageIndex = 0; searchOrigin = null;
        reader.Clear(); chapters.Items.Clear(); bookmarks.Items.Clear(); results.Items.Clear();
        if (readerTitle != null) readerTitle.Text = "";
        loading = false;
    }

    // PARAFORMAT2 offsets and flags follow the Windows SDK Richedit.h definition.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 188)]
    private struct ParagraphFormat
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Size;
        [System.Runtime.InteropServices.FieldOffset(4)] public uint Mask;
        [System.Runtime.InteropServices.FieldOffset(160)] public int SpaceAfter;
        [System.Runtime.InteropServices.FieldOffset(164)] public int LineSpacing;
        [System.Runtime.InteropServices.FieldOffset(170)] public byte Rule;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendParagraphFormat(IntPtr handle, int message, IntPtr wParam, ref ParagraphFormat format);

    private void ApplyParagraphLayout()
    {
        if (!reader.IsHandleCreated || reader.TextLength == 0) return;
        bool wasLoading = loading; loading = true;
        int start = reader.SelectionStart, length = reader.SelectionLength, offset = GetVisibleOffset();
        try
        {
            reader.SelectAll();
            var format = new ParagraphFormat { Size = 188, Mask = 0x100 | 0x80, LineSpacing = (int)(Math.Clamp(settings.LineSpacing, 1m, 3m) * 20), Rule = 5, SpaceAfter = Math.Clamp(settings.ParagraphSpacing, 0, 40) * 20 };
            if (SendParagraphFormat(reader.Handle, 0x0447, IntPtr.Zero, ref format) == IntPtr.Zero) throw new InvalidOperationException("正文排版设置失败。");
            ScrollToOffset(offset); reader.Select(start, length);
        }
        finally { loading = wasLoading; }
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
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }
    private void ApplyMode() => Reflow();

    private void RefreshChapters()
    {
        bool wasLoading = loading; loading = true;
        chapters.BeginUpdate();
        try
        {
            chapters.Items.Clear();
            if (currentBook != null)
            {
                string filter = chapterFilter.Text.Trim();
                chapters.Items.AddRange(currentBook.Chapters.Where(c => c.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray());
                if (currentChapter >= 0 && currentChapter < currentBook.Chapters.Count) chapters.SelectedItem = currentBook.Chapters[currentChapter];
            }
        }
        finally { chapters.EndUpdate(); loading = wasLoading; }
    }

    private void GoChapterNumber()
    {
        if (currentBook == null || !int.TryParse(chapterNumber.Text.Trim(), out int number) || number < 1) { chapterNumber.BackColor = Color.MistyRose; return; }
        int index = currentBook.Chapters.FindIndex(c => ReadingTools.ChapterNumber(c.Title) == number);
        if (index < 0 && !currentBook.Chapters.Any(c => ReadingTools.ChapterNumber(c.Title) != null) && number <= currentBook.Chapters.Count) index = number - 1;
        if (index < 0) { chapterNumber.BackColor = Color.MistyRose; return; }
        chapterNumber.BackColor = SystemColors.Window;
        chapterFilter.Clear(); OpenChapter(index, 0); reader.Focus();
    }

    private void MoveSearchResult(int direction)
    {
        int start = results.SelectedIndex;
        for (int index = start < 0 ? (direction > 0 ? 0 : results.Items.Count - 1) : start + direction; index >= 0 && index < results.Items.Count; index += direction)
            if (results.Items[index] is SearchHit hit && hit.BookId == currentBook?.Id) { results.SelectedIndex = index; JumpSearch(); return; }
    }

    private void ReturnFromSearch()
    {
        if (searchOrigin is not { } origin || currentBook?.Id != origin.BookId) return;
        searchOrigin = null; OpenChapter(origin.Chapter, origin.Offset); reader.Focus();
    }

    private void OpenSearch()
    {
        if (focusMode) ToggleFocusMode();
        mainSplit.Panel1Collapsed = false; directoryButton.Text = "收起目录";
        sidebarTabs.SelectedIndex = 2; searchBox.Focus();
    }

    private void HandleShortcut(object? sender, KeyEventArgs e)
    {
        Keys pressed = e.KeyCode | e.Modifiers;
        bool handled = true;
        if (focusMode && e.KeyCode == Keys.Escape) ToggleFocusMode();
        else if (pressed == (Keys)settings.HideWindowHotkey) HideToTray();
        else if (pressed == (Keys)settings.FocusModeHotkey) ToggleFocusMode();
        else if (pressed == (Keys)settings.DirectoryHotkey) ToggleDirectoryDrawer();
        else
        {
            string? command = ReadingTools.Shortcuts.Keys.FirstOrDefault(c => ReadingTools.Shortcut(settings, c) == pressed);
            bool typing = chapterFilter.Focused || chapterNumber.Focused || searchBox.Focused || fontSize.ContainsFocus || mode.ContainsFocus || theme.ContainsFocus;
            if (command == null && reader.Focused && pressed is Keys.PageUp or Keys.PageDown) command = pressed == Keys.PageUp ? "PreviousPage" : "NextPage";
            if (command == null || (typing && (e.Modifiers == Keys.None || command is "PreviousPage" or "NextPage" or "PreviousChapter" or "NextChapter" or "ScrollUp" or "ScrollDown" or "ChapterStart" or "ChapterEnd"))) handled = false;
            else switch (command)
            {
                case "PreviousPage": ScrollLines(-VisibleLineCount()); break;
                case "NextPage": ScrollLines(VisibleLineCount()); break;
                case "PreviousChapter": PreviousChapter(); break;
                case "NextChapter": NextChapter(); break;
                case "ScrollUp": ScrollLines(-3); break;
                case "ScrollDown": ScrollLines(3); break;
                case "ChapterStart": if (reader.Paged) ShowPage(0); else ScrollToOffset(0); SavePosition(); break;
                case "ChapterEnd": if (reader.Paged) ShowPage(pageStarts.Count - 1); else ScrollToOffset(chapterContent.Length); SavePosition(); break;
                case "Bookmark": AddBookmark(this, EventArgs.Empty); break;
                case "Search": OpenSearch(); break;
                case "PreviousResult": MoveSearchResult(-1); break;
                case "NextResult": MoveSearchResult(1); break;
                case "Return": ReturnFromSearch(); break;
                case "GoChapter": if (focusMode) ToggleFocusMode(); mainSplit.Panel1Collapsed = false; sidebarTabs.SelectedIndex = 1; chapterNumber.Focus(); break;
            }
        }
        if (handled) { e.SuppressKeyPress = true; e.Handled = true; }
    }

    private void ScheduleReflow()
    {
        if (reflowPending || !IsHandleCreated || closing) return;
        reflowPending = true;
        int offset = reader.Paged ? pageStarts[pageIndex] : GetVisibleOffset();
        BeginInvoke(() => { reflowPending = false; if (!closing && !IsDisposed) Reflow(offset); });
    }

    private void Reflow(int? requestedOffset = null)
    {
        if (currentBook == null || currentChapter < 0) return;
        int offset = Math.Clamp(requestedOffset ?? currentBook.LastOffset, 0, chapterContent.Length);
        bool wasLoading = loading; loading = true;
        try
        {
            reader.Paged = settings.Mode == "翻页";
            reader.ScrollBars = reader.Paged ? RichTextBoxScrollBars.None : RichTextBoxScrollBars.Vertical;
            reader.Text = chapterContent;
            pageStarts = [0]; pageIndex = 0;
            ApplyParagraphLayout();
            if (reader.Paged)
            {
                using var layout = new RichTextBox { Font = reader.Font, Size = reader.ClientSize, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.None, Rtf = reader.Rtf };
                pageStarts = ReadingTools.Paginate(layout, reader.ClientSize);
                int index = pageStarts.FindLastIndex(start => start <= offset);
                ShowPage(Math.Max(0, index));
            }
            else ScrollToOffset(offset);
        }
        finally { loading = wasLoading; }
    }

    private void ShowPage(int index)
    {
        pageIndex = Math.Clamp(index, 0, pageStarts.Count - 1);
        int start = pageStarts[pageIndex], end = pageIndex + 1 < pageStarts.Count ? pageStarts[pageIndex + 1] : chapterContent.Length;
        bool wasLoading = loading; loading = true;
        try { reader.Text = chapterContent[start..end]; ApplyParagraphLayout(); reader.Select(0, 0); reader.ScrollToCaret(); }
        finally { loading = wasLoading; }
        if (readerTitle != null && currentBook != null) readerTitle.Text = $"{currentBook.Title} · {currentBook.Chapters[currentChapter].Title} · 第{pageIndex + 1}/{pageStarts.Count}页";
        if (!loading) SavePosition();
    }

    private void TurnPage(int direction)
    {
        if (currentBook == null) return;
        if (!reader.Paged) { ScrollLines(direction * VisibleLineCount()); return; }
        if (pageIndex + direction >= 0 && pageIndex + direction < pageStarts.Count) ShowPage(pageIndex + direction);
        else if (direction > 0) NextChapter();
        else if (currentChapter > 0) OpenChapter(currentChapter - 1, int.MaxValue);
    }

    internal int ResizeHit(Point screenPoint)
    {
        if (!focusMode) return 0;
        var point = PointToClient(screenPoint);
        int edge = Math.Max(6, DeviceDpi / 12);
        bool left = point.X < edge, right = point.X >= ClientSize.Width - edge, top = point.Y < edge, bottom = point.Y >= ClientSize.Height - edge;
        return top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 0;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    private void BeginWindowAction(int hit)
    {
        if (!focusMode) return;
        Point point = Cursor.Position;
        int coordinates = (point.X & 0xffff) | ((point.Y & 0xffff) << 16);
        ReleaseCapture(); SendMessage(Handle, 0x00A1, (IntPtr)hit, (IntPtr)coordinates);
    }
    protected override void WndProc(ref Message m)
    {
        if (focusMode && m.Msg == 0x0084) { int hit = ResizeHit(new Point(unchecked((int)m.LParam))); if (hit != 0) { m.Result = (IntPtr)hit; return; } }
        base.WndProc(ref m);
    }
}


internal sealed class ReaderSettingsDialog : Form
{
    private readonly Dictionary<string, HotkeyTextBox> shortcuts = [];
    private readonly NumericUpDown textMargin, lineSpacing, paragraphSpacing;
    public int TextMargin => (int)textMargin.Value;
    public decimal LineSpacing => lineSpacing.Value;
    public int ParagraphSpacing => (int)paragraphSpacing.Value;
    public Keys FocusModeKey => shortcuts["Focus"].Hotkey;
    public Keys HideWindowKey => shortcuts["Hide"].Hotkey;
    public Keys DirectoryKey => shortcuts["Directory"].Hotkey;
    public Dictionary<string, int> NavigationHotkeys => ReadingTools.Shortcuts.Keys.ToDictionary(c => c, c => (int)shortcuts[c].Hotkey);
    public ReaderSettingsDialog(ReaderSettings settings)
    {
        Text = "Read_me 设置"; ClientSize = new Size(460, 620); StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        void Row(string label, Control control)
        {
            int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); grid.Controls.Add(control, 1, row);
        }
        void Shortcut(string id, string label, Keys key) { var input = new HotkeyTextBox(key); shortcuts[id] = input; Row(label, input); }
        Shortcut("Focus", "专注模式", (Keys)settings.FocusModeHotkey); Shortcut("Hide", "隐藏到托盘", (Keys)settings.HideWindowHotkey); Shortcut("Directory", "展开／收起目录", (Keys)settings.DirectoryHotkey);
        foreach (var item in ReadingTools.Shortcuts) Shortcut(item.Key, item.Value.Label, ReadingTools.Shortcut(settings, item.Key));
        textMargin = new NumericUpDown { Minimum = 0, Maximum = 100, Value = Math.Clamp(settings.TextMargin, 0, 100), Dock = DockStyle.Fill };
        lineSpacing = new NumericUpDown { Minimum = 1, Maximum = 3, DecimalPlaces = 2, Increment = 0.05m, Value = Math.Clamp(settings.LineSpacing, 1m, 3m), Dock = DockStyle.Fill };
        paragraphSpacing = new NumericUpDown { Minimum = 0, Maximum = 40, Value = Math.Clamp(settings.ParagraphSpacing, 0, 40), Dock = DockStyle.Fill };
        Row("正文留白（像素）", textMargin); Row("行距（倍数）", lineSpacing); Row("段落下方间距（磅）", paragraphSpacing);
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scroller.Controls.Add(grid);
        var error = new Label { Dock = DockStyle.Bottom, Height = 40, ForeColor = Color.Firebrick };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存" }; var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            var keys = shortcuts.Values.Select(s => s.Hotkey).ToArray();
            if (keys.Any(k => k == Keys.None || (k & Keys.KeyCode) == Keys.Escape)) { error.Text = "快捷键不能为空；Esc保留用于退出专注模式。"; return; }
            if (keys.Distinct().Count() != keys.Length) { error.Text = "快捷键不能重复。"; return; }
            if (shortcuts.Where(p => p.Key is "Focus" or "Hide" or "Directory" or "Bookmark" or "Search" or "PreviousResult" or "NextResult" or "Return" or "GoChapter").Any(p => (p.Value.Hotkey & Keys.Modifiers) == 0 && (p.Value.Hotkey & Keys.KeyCode) < Keys.F1)) { error.Text = "全局操作请使用组合键或功能键，避免影响输入。"; return; }
            DialogResult = DialogResult.OK; Close();
        };
        buttons.Controls.AddRange([save, cancel]); Controls.Add(scroller); Controls.Add(error); Controls.Add(buttons); CancelButton = cancel;
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
