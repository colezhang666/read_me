using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using PocketReader;

internal static class Program
{
    static void Check(bool okay, string message)
    {
        if (!okay) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    [STAThread]
    static void Main(string[] args)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "ReadMeCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string gap = Path.Combine(scratch, "gap.txt");
            File.WriteAllText(gap, "第5章 开始\n内容甲\n第7章 缺章\n内容乙\n第八章 中文\n内容丙\n番外 一\n内容丁\n");
            if (args.Length > 0 && args[0] == "--baseline")
            {
                var context = new AssemblyLoadContext("baseline", true);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
                var importer = assembly.GetType("PocketReader.TxtImporter")!;
                var book = importer.GetMethod("Import")!.Invoke(null, new object[] { gap, scratch })!;
                var chapters = (System.Collections.ICollection)book.GetType().GetProperty("Chapters")!.GetValue(book)!;
                Check(chapters.Count == 1, "复现旧版：缺章、非第1章开头和中文章号被合并成一章");
                return;
            }
            var imported = TxtImporter.Import(gap, scratch);
            Check(imported.Chapters.Count == 4, "非第1章开头、缺章、中文章号、番外识别");
            string prefix = Path.Combine(scratch, "prefix.txt");
            File.WriteAllText(prefix, "作者说明\n第十一章 开始\n正文\n");
            var preamble = TxtImporter.Import(prefix, scratch);
            Check(preamble.Chapters.Count == 2 && File.ReadAllText(Path.Combine(scratch, preamble.Id, preamble.Chapters[0].FileName)).Contains("作者说明"), "前言内容保留");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string gb = Path.Combine(scratch, "gb.txt");
            File.WriteAllText(gb, "第一章 测试\n中文正文\n第二章 继续\n更多正文", Encoding.GetEncoding("GB18030"));
            Check(TxtImporter.Import(gb, scratch).Chapters.Count == 2, "GB18030 编码识别");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                int before = Directory.GetDirectories(scratch).Length;
                try { TxtImporter.Import(gap, scratch, token: cancel.Token); throw new Exception("取消未生效"); }
                catch (OperationCanceledException) { Check(Directory.GetDirectories(scratch).Length == before, "导入取消清理临时书籍"); }
            }
            string customPath = Path.Combine(scratch, "english.txt");
            File.WriteAllText(customPath, "Chapter 1 One\nText A\nChapter 2 Two\nText B\n");
            var customBook = TxtImporter.Import(customPath, scratch, options: new ImportOptions("utf-8", "自定义正则", @"^Chapter\s+\d+.*$"));
            Check(customBook.Chapters.Count == 2, "自定义章节规则识别英文标题");
            Check(TxtImporter.Import(customPath, scratch, options: new ImportOptions("utf-8", "不分章")).Chapters.Count == 1, "不分章选项保留完整正文");
            try { _ = new ImportOptions("自动", "自定义正则", "["); throw new Exception("应拒绝无效正则"); } catch (ArgumentException) { Console.WriteLine("PASS 无效导入规则拒绝执行"); }
            Check(ReadingTools.ChapterNumber("第八章 测试") == 8 && ReadingTools.ChapterNumber("第一千零七十章 结束") == 1070, "中文章号转换");
            string store = Path.Combine(scratch, "store");
            LibraryStore.Save(new ReaderSettings { FontSize = 18 }, store);
            LibraryStore.Save(new ReaderSettings { FontSize = 23 }, store);
            Check(LibraryStore.Load(store).FontSize == 23, "存档往返读取");
            File.WriteAllText(Path.Combine(store, "settings.json"), "{broken");
            Check(LibraryStore.Load(store).FontSize == 18 && LibraryStore.RecoveryNotice != null, "损坏存档恢复上一份备份");
            Check(Directory.GetFiles(store, "*.damaged-*").Length == 1, "损坏文件保留");

            if (args.Length > 0)
            {
                var novel = TxtImporter.Import(args[0], scratch);
                Check(novel.Chapters.Count == 1071, "原小说前言和1070章均识别");
                var abnormal = novel.Chapters.Single(c => c.Title == "第448节");
                Check(File.ReadAllText(Path.Combine(scratch, novel.Id, abnormal.FileName)).Length > 0, "第448节同行正文保留");
                using var a = File.OpenRead(args[0]);
                using var b = File.OpenRead(Path.Combine(scratch, novel.Id, "original.txt"));
                Check(SHA256.HashData(a).SequenceEqual(SHA256.HashData(b)), "原小说副本字节一致");
            }

            // The test executable owns its ReaderData directory; never use a published app's library.
            string testRoot = LibraryStore.Root;
            if (Directory.Exists(testRoot)) throw new Exception("测试目录已有 ReaderData，请先检查测试残留。");
            try
            {
                var first = TxtImporter.Import(gap, testRoot);
                var second = TxtImporter.Import(prefix, testRoot);
                File.WriteAllText(LibraryStore.ChapterPath(first, 0), string.Join('\n', Enumerable.Range(0, 500).Select(i => $"第{i:D4}行 测试正文 abcdefghijklmnopqrstuvwxyz")));
                first.LastChapter = 0; first.LastOffset = 3500;
                var settings = new ReaderSettings { Books = [first, second], CurrentBookId = first.Id, WindowX = 80, WindowY = 90, WindowWidth = 900, WindowHeight = 650, SidebarWidth = 220 };
                LibraryStore.Save(settings);
                ApplicationConfiguration.Initialize();
                using var form = new ReaderForm();
                form.Show(); Application.DoEvents();
                var reader = Field<RichTextBox>(form, "reader");
                var live = Field<ReaderSettings>(form, "settings");
                var chapters = Field<ListBox>(form, "chapters");
                Check(reader.GetLineFromCharIndex((int)Call(form, "GetVisibleOffset")!) == reader.GetLineFromCharIndex(3500), "启动精确恢复章内所在行");
                Check(form.Width == 900 && form.Height == 650, "窗口尺寸恢复");
                Call(form, "OpenChapter", 2, 0);
                Check(chapters.SelectedIndex == 2, "程序跳章同步目录高亮");
                var results = Field<ListBox>(form, "results");
                results.Items.Add(new SearchHit { BookId = second.Id, Chapter = 0, Offset = 0 });
                results.SelectedIndex = 0;
                Call(form, "JumpSearch");
                Check(Field<int>(form, "currentChapter") == 2, "不同书籍的搜索结果不能跳转");
                var books = Field<ListBox>(form, "books");
                books.SelectedIndex = 1;
                Check(results.Items.Count == 0, "切换书籍清空搜索结果");
                books.SelectedIndex = 0;
                Call(form, "OpenChapter", 0, 3500);
                var writeBefore = File.GetLastWriteTimeUtc(Path.Combine(testRoot, "settings.json")); Call(form, "SavePosition"); Call(form, "SavePosition"); Check(File.GetLastWriteTimeUtc(Path.Combine(testRoot, "settings.json")) == writeBefore, "滚动保存合并写入而非每次立即写盘"); Call(form, "SaveState");
                var saved = LibraryStore.Load();
                Check(saved.Books[0].LastOffset > 0, "保存章内可见位置");
                var bounds = form.Bounds;
                Call(form, "ToggleFocusMode");
                Check(form.Bounds == bounds && form.FormBorderStyle == FormBorderStyle.None && Field<SplitContainer>(form, "mainSplit").Panel1Collapsed, "专注模式保持窗口大小并隐藏标题栏和边栏");
                Call(form, "ToggleFocusMode");
                Check(form.Bounds == bounds && form.FormBorderStyle != FormBorderStyle.None, "退出专注模式恢复窗口");
                var hits = BookSearch.Search(first, "测试正文", 5);
                Check(hits.Count == 5 && hits.All(x => x.BookId == first.Id), "搜索限制和书籍身份绑定");
                Call(form, "OpenChapter", 0, 0);
                string unchangedText = reader.Text;
                int secondLine = reader.GetFirstCharIndexFromLine(1);
                int normalGap = reader.GetPositionFromCharIndex(secondLine).Y - reader.GetPositionFromCharIndex(0).Y;
                live.LineSpacing = 1.8m; live.ParagraphSpacing = 6;
                Call(form, "ApplyParagraphLayout");
                int expandedGap = reader.GetPositionFromCharIndex(secondLine).Y - reader.GetPositionFromCharIndex(0).Y;
                Check(expandedGap > normalGap && reader.Text == unchangedText, "原生行距和段距生效且不修改正文");
                var formatType = typeof(ReaderForm).GetNestedType("ParagraphFormat", BindingFlags.NonPublic)!;
                object format = Activator.CreateInstance(formatType)!;
                formatType.GetField("Size")!.SetValue(format, (uint)188);
                object[] formatArgs = [reader.Handle, 0x043D, IntPtr.Zero, format];
                typeof(ReaderForm).GetMethod("SendParagraphFormat", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, formatArgs);
                Check((int)formatType.GetField("LineSpacing")!.GetValue(formatArgs[3])! == 36 && (int)formatType.GetField("SpaceAfter")!.GetValue(formatArgs[3])! == 120, "读取Windows原生排版参数验证1.8倍行距和6磅段距");
                var activeBook = live.Books[0];
                string bookId = activeBook.Id;
                Check(!(bool)Call(form, "RenameBook", activeBook, "   ")!, "空书名被拒绝");
                Check((bool)Call(form, "RenameBook", activeBook, "我的测试书")! && LibraryStore.Load().Books[0].Title == "我的测试书", "书籍重命名持久化");
                Call(form, "OpenChapter", 0, saved.Books[0].LastOffset);
                activeBook.Bookmarks.Add(new BookmarkRecord { Chapter = 0, Offset = 120, Label = "测试书签" });
                Call(form, "RemoveBook", activeBook);
                Check(live.Books.All(b => b.Id != bookId) && live.RemovedBooks.Single().Id == bookId && File.Exists(LibraryStore.ChapterPath(activeBook, 0)), "移入回收站保留正文文件");
                Check(Field<BookRecord>(form, "currentBook").Id != bookId, "移除当前书后切换到剩余书籍");
                Call(form, "RestoreBook", activeBook);
                Check(live.RemovedBooks.Count == 0 && activeBook.Bookmarks.Single().Offset == 120 && activeBook.LastOffset > 0, "恢复书籍保留书签和章内进度");
                Call(form, "SaveState");
                Check(LibraryStore.Load().LineSpacing == 1.8m && LibraryStore.Load().ParagraphSpacing == 6, "排版设置持久化");
                Check(LibraryStore.Load().RemovedBooks.Count == 0 && LibraryStore.Load().Books.Any(b => b.Id == bookId), "恢复后的书架状态持久化");
                foreach (var item in live.Books.ToArray()) Call(form, "RemoveBook", item);
                Check(live.Books.Count == 0 && reader.TextLength == 0 && chapters.Items.Count == 0, "移除最后一本书清空正文和目录");
                Call(form, "RestoreBook", activeBook);
                Call(form, "OpenChapter", 0, 3500);
                var filter = Field<TextBox>(form, "chapterFilter");
                filter.Text = "缺章";
                Check(chapters.Items.Count == 1 && Field<int>(form, "currentChapter") == 0, "目录筛选不改变阅读位置");
                chapters.SelectedIndex = 0;
                Check(Field<int>(form, "currentChapter") == 1, "筛选后的目录映射原始章节");
                var numberBox = Field<TextBox>(form, "chapterNumber"); numberBox.Text = "8"; Call(form, "GoChapterNumber");
                Check(Field<int>(form, "currentChapter") == 2, "输入章号跳转中文标题");
                numberBox.Text = "999"; Call(form, "GoChapterNumber"); Check(Field<int>(form, "currentChapter") == 2, "无效章号不改变阅读位置");
                Call(form, "OpenChapter", 0, 3500);
                int origin = (int)Call(form, "GetVisibleOffset")!;
                results.Items.Clear(); results.Items.Add(new SearchHit { BookId = activeBook.Id, Chapter = 1, Offset = 0 }); results.Items.Add(new SearchHit { BookId = activeBook.Id, Chapter = 2, Offset = 0 });
                results.SelectedIndex = -1; Call(form, "MoveSearchResult", 1);
                Check(Field<int>(form, "currentChapter") == 1, "下一个搜索结果跳转");
                Call(form, "MoveSearchResult", 1); Check(Field<int>(form, "currentChapter") == 2, "继续跳转下一个结果");
                Call(form, "MoveSearchResult", -1); Check(Field<int>(form, "currentChapter") == 1, "上一个搜索结果跳转");
                Call(form, "ReturnFromSearch"); Check(Field<int>(form, "currentChapter") == 0 && (int)Call(form, "GetVisibleOffset")! == origin, "返回搜索前的章节和精确位置");
                live.NavigationHotkeys["NextChapter"] = (int)(Keys.Control | Keys.N);
                reader.Focus(); Call(form, "HandleShortcut", form, new KeyEventArgs(Keys.Control | Keys.N)); Check(Field<int>(form, "currentChapter") == 1, "自定义切章快捷键执行");
                filter.Focus(); filter.Text = ""; Call(form, "HandleShortcut", form, new KeyEventArgs(Keys.Right)); Check(Field<int>(form, "currentChapter") == 1, "文本输入框方向键不触发翻页");
                var strip = Field<ToolStrip>(form, "toolbar");
                int bookmarkCount = activeBook.Bookmarks.Count;
                strip.Items.OfType<ToolStripButton>().Single(b => b.Text == "加书签").PerformClick();
                Check(activeBook.Bookmarks.Count == bookmarkCount + 1, "工具栏折叠菜单按钮可执行操作");
                Call(form, "OpenChapter", 0, 0);
                live.Mode = "翻页"; Call(form, "Reflow", (int?)0);
                var pages = Field<List<int>>(form, "pageStarts");
                string content = Field<string>(form, "chapterContent");
                Check(pages.Count > 1 && pages[0] == 0 && pages.Zip(pages.Skip(1)).All(p => p.First < p.Second), "真正分页生成连续有序的页面边界");
                var reconstructed = new StringBuilder();
                for (int p = 0; p < pages.Count; p++) { Call(form, "ShowPage", p); reconstructed.Append(reader.Text); }
                Check(reconstructed.ToString() == content, "逐页拼接与整章完全一致，没有丢字和重复");
                Call(form, "ShowPage", 0); Call(form, "TurnPage", 1);
                Check(Field<int>(form, "pageIndex") == 1 && (int)Call(form, "GetVisibleOffset")! == pages[1], "下一页切换固定页面并保存全章偏移");
                int pageOffset = pages[1];
                Call(form, "AddBookmark", form, EventArgs.Empty); Check(activeBook.Bookmarks.Last().Offset == pageOffset, "分页书签记录全章位置");
                Call(form, "ShowPage", 0); Field<ListBox>(form, "bookmarks").SelectedIndex = Field<ListBox>(form, "bookmarks").Items.Count - 1; Call(form, "JumpBookmark");
                Check(Field<int>(form, "pageIndex") == 1, "分页书签跳回对应页面");
                Call(form, "Reflow", (int?)pageOffset); Check(Field<List<int>>(form, "pageStarts")[Field<int>(form, "pageIndex")] <= pageOffset, "重新分页保留原位置所在页");
                // The visible page must fit, rather than relying on a hidden scrollbar.
                Call(form, "ShowPage", 1);
                int bottomY = reader.GetPositionFromCharIndex(Math.Max(0, reader.TextLength - 1)).Y;
                Check(bottomY + reader.Font.Height <= reader.ClientSize.Height, "分页内容在视口内完整显示");
                Call(form, "ShowPage", pages.Count - 1); Call(form, "TurnPage", 1);
                Check(Field<int>(form, "currentChapter") == 1, "末页翻到下一章");
                Call(form, "TurnPage", -1); Check(Field<int>(form, "currentChapter") == 0 && Field<int>(form, "pageIndex") == Field<List<int>>(form, "pageStarts").Count - 1, "章首页回到上一章末页");
                var oldBounds = form.Bounds; Call(form, "ToggleFocusMode");
                Check((int)Call(form, "ResizeHit", form.PointToScreen(new Point(1,1)))! == 13, "专注窗口左上角支持原生缩放命中");
                form.Size = new Size(520, 400); Application.DoEvents();
                Check(form.Size == new Size(520,400), "专注模式可调整窗口尺寸");
                Call(form, "ToggleFocusMode"); Check(form.Size == new Size(520,400), "退出专注保留调整后的窗口尺寸"); form.Bounds = oldBounds; Application.DoEvents();
                live.Mode = "滚动"; Call(form, "Reflow", (int?)saved.Books[0].LastOffset);
                Call(form, "SaveState");
                using (var previewDialog = (Form)Activator.CreateInstance(typeof(ReaderForm).Assembly.GetType("PocketReader.ImportPreviewDialog")!, customPath)!)
                {
                    previewDialog.Show();
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    var acceptButton = Field<Button>(previewDialog, "accept");
                    while (!acceptButton.Enabled && wait.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(1); }
                    Check(acceptButton.Enabled && Field<ListBox>(previewDialog, "chapterList").Items.Count > 0, "导入预览在后台完成并展示目录");
                    Field<ComboBox>(previewDialog, "rule").SelectedItem = "自定义正则";
                    Field<TextBox>(previewDialog, "pattern").Text = @"^Chapter\s+\d+.*$";
                    Check(!acceptButton.Enabled, "调整导入规则后必须重新预览");
                    var task = (Task)Call(previewDialog, "RefreshPreview")!;
                    wait.Restart(); while (!task.IsCompleted && wait.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(1); }
                    Check(task.IsCompletedSuccessfully && acceptButton.Enabled && Field<ListBox>(previewDialog, "chapterList").Items.Count == 2, "调整规则后的预览与最终导入一致");
                    previewDialog.Close();
                }
                form.Close();
                using var reopened = new ReaderForm();
                reopened.Show(); Application.DoEvents();
                var reopenedReader = Field<RichTextBox>(reopened, "reader"); Check(reopenedReader.GetLineFromCharIndex((int)Call(reopened, "GetVisibleOffset")!) == reopenedReader.GetLineFromCharIndex(saved.Books[0].LastOffset), "退出再打开精确恢复所在行");
                var reopenedLive = Field<ReaderSettings>(reopened, "settings"); Check(reopenedLive.LineSpacing == 1.8m && reopenedReader.GetPositionFromCharIndex(reopenedReader.GetFirstCharIndexFromLine(1)).Y - reopenedReader.GetPositionFromCharIndex(0).Y > normalGap, "重开自动应用保存的排版设置"); reopenedLive.Mode = "翻页"; Call(reopened, "Reflow", (int?)3500); Call(reopened, "SaveState"); int pageSaved = (int)Call(reopened, "GetVisibleOffset")!; reopened.Close();
                using var pagedReopened = new ReaderForm(); pagedReopened.Show(); Application.DoEvents();
                Check(Field<ReaderSettings>(pagedReopened, "settings").Mode == "翻页" && (int)Call(pagedReopened, "GetVisibleOffset")! == pageSaved, "退出重开保留分页模式与页面进度");
                pagedReopened.Close();
            }
            finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
        }
        finally { Directory.Delete(scratch, true); }
    }
}
