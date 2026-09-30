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
                form.Close();
                using var reopened = new ReaderForm();
                reopened.Show(); Application.DoEvents();
                var reopenedReader = Field<RichTextBox>(reopened, "reader"); Check(reopenedReader.GetLineFromCharIndex((int)Call(reopened, "GetVisibleOffset")!) == reopenedReader.GetLineFromCharIndex(saved.Books[0].LastOffset), "退出再打开精确恢复所在行");
                var reopenedLive = Field<ReaderSettings>(reopened, "settings"); Check(reopenedLive.LineSpacing == 1.8m && reopenedReader.GetPositionFromCharIndex(reopenedReader.GetFirstCharIndexFromLine(1)).Y - reopenedReader.GetPositionFromCharIndex(0).Y > normalGap, "重开自动应用保存的排版设置"); reopened.Close();
            }
            finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
        }
        finally { Directory.Delete(scratch, true); }
    }
}
