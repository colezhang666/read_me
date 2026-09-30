using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PocketReader;

public sealed class ReaderSettings
{
    public List<BookRecord> Books { get; set; } = [];
    public List<BookRecord> RemovedBooks { get; set; } = [];
    public string? CurrentBookId { get; set; }
    public string Mode { get; set; } = "滚动";
    public string Theme { get; set; } = "浅色";
    public float FontSize { get; set; } = 17;
    public string FontFamilyName { get; set; } = "Microsoft YaHei UI";
    public int TextMargin { get; set; } = 12;
    public decimal LineSpacing { get; set; } = 1;
    public int ParagraphSpacing { get; set; }
    public int FocusModeHotkey { get; set; } = (int)Keys.F11;
    public int HideWindowHotkey { get; set; } = (int)(Keys.Control | Keys.H);
    public int DirectoryHotkey { get; set; } = (int)(Keys.Control | Keys.D);
    public Dictionary<string, int> NavigationHotkeys { get; set; } = [];
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public int SidebarWidth { get; set; } = 285;
}

public sealed class BookRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public List<ChapterRecord> Chapters { get; set; } = [];
    public int LastChapter { get; set; }
    public int LastOffset { get; set; }
    public List<BookmarkRecord> Bookmarks { get; set; } = [];
    public override string ToString() => Title;
}

public sealed class ChapterRecord
{
    public string Title { get; set; } = "";
    public string FileName { get; set; } = "";
    public override string ToString() => Title;
}

public sealed class BookmarkRecord
{
    public int Chapter { get; set; }
    public int Offset { get; set; }
    public string Label { get; set; } = "";
    public override string ToString() => Label;
}

public sealed class SearchHit
{
    public string BookId { get; init; } = "";
    public int Chapter { get; init; }
    public int Offset { get; init; }
    public string Display { get; init; } = "";
    public override string ToString() => Display;
}

public static class LibraryStore
{
    public static readonly string Root = Path.Combine(AppContext.BaseDirectory, "ReaderData");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string? RecoveryNotice { get; private set; }

    public static ReaderSettings Load(string? root = null)
    {
        string path = Path.Combine(root ?? Root, "settings.json");
        RecoveryNotice = null;
        Directory.CreateDirectory(root ?? Root);
        if (!File.Exists(path)) return new ReaderSettings();
        try { return Read(path); }
        catch (JsonException)
        {
            if (!File.Exists(path + ".bak")) throw new InvalidDataException("书库设置文件损坏，且没有可恢复的备份。请保留 ReaderData 文件夹。");
            var recovered = Read(path + ".bak");
            File.Copy(path, path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true);
            File.Copy(path + ".bak", path, true);
            RecoveryNotice = "存档损坏，已恢复上一份备份；损坏文件也已保留。最近一次进度可能需要重新定位。";
            return recovered;
        }

        static ReaderSettings Read(string path) => JsonSerializer.Deserialize<ReaderSettings>(File.ReadAllText(path))
            ?? throw new JsonException("存档内容为空。");
    }

    public static void Save(ReaderSettings settings, string? root = null)
    {
        Directory.CreateDirectory(root ?? Root);
        string path = Path.Combine(root ?? Root, "settings.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }

    public static string ChapterPath(BookRecord book, int index) =>
        Path.Combine(Root, book.Id, book.Chapters[index].FileName);

    public static string ReadChapter(BookRecord book, int index) =>
        File.ReadAllText(ChapterPath(book, index), Encoding.UTF8);
}

public static partial class TxtImporter
{
    [GeneratedRegex(@"^\s*第\s*([\d零〇一二三四五六七八九十百千万两]+)\s*([章回节])(?:[\s、：:.．-]*(.*))?\s*$")]
    private static partial Regex ChapterHeading();
    [GeneratedRegex(@"^\s*(?:序章|序言|前言|楔子|后记|尾声|番外)(?:[\s\d零〇一二三四五六七八九十、：:.．-].*)?\s*$")]
    private static partial Regex ExtraHeading();

    public static BookRecord Import(string sourcePath, string root, IProgress<string>? progress = null, CancellationToken token = default, ImportOptions? options = null)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到 TXT 文件。", sourcePath);
        Directory.CreateDirectory(root);
        byte[] hash;
        using (var source = File.OpenRead(sourcePath)) hash = SHA256.HashData(source);
        var book = new BookRecord
        {
            Title = Path.GetFileNameWithoutExtension(sourcePath),
            Fingerprint = Convert.ToHexString(hash)
        };
        string bookPath = Path.Combine(root, book.Id);
        Directory.CreateDirectory(bookPath);
        try
        {
            progress?.Report("复制原文件…");
            File.Copy(sourcePath, Path.Combine(bookPath, "original.txt"));
            progress?.Report("识别编码和章节…");
            using var reader = new StreamReader(sourcePath, GetEncoding(sourcePath, options), options?.EncodingName == null || options.EncodingName == "自动");
            var body = new StringBuilder();
            string currentTitle = "书籍信息";
            bool haveChapter = false;
            int previousNumber = 0;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                token.ThrowIfCancellationRequested();
                var match = ChapterHeading().Match(line);
                bool numbered = match.Success;
                bool arabic = int.TryParse(match.Groups[1].Value, out int chapterNumber);
                // ponytail: short heading heuristic; add an import preview if unusual books need manual rules.
                bool longInlineHeading = numbered && line.Length > 120 && arabic && chapterNumber == previousNumber + 1;
                bool custom = options?.Pattern is { Length: > 0 };
                bool detected = custom ? options!.HeadingRegex!.IsMatch(line) : options?.Rule != "不分章" && ((numbered && (line.Length <= 120 || longInlineHeading)) || (line.Length <= 80 && ExtraHeading().IsMatch(line)));
                if (detected)
                {
                    string headingText = match.Groups[3].Value.Trim();
                    bool longHeading = !custom && numbered && headingText.Length > 40;
                    if (haveChapter) Flush();
                    else if (!string.IsNullOrWhiteSpace(body.ToString())) Flush();
                    else body.Clear();
                    haveChapter = true;
                    if (arabic) previousNumber = chapterNumber;
                    currentTitle = longHeading
                        ? $"第{match.Groups[1].Value}{match.Groups[2].Value}"
                        : line.Trim();
                    if (longHeading) body.AppendLine(headingText);
                }
                else body.AppendLine(line);
            }
            if (haveChapter || body.Length > 0) Flush();
            if (book.Chapters.Count == 0) throw new InvalidDataException("TXT 文件没有可阅读的内容。");
            return book;

            void Flush()
            {
                var fileName = $"{book.Chapters.Count:D5}.txt";
                File.WriteAllText(Path.Combine(bookPath, fileName), body.ToString(), new UTF8Encoding(false));
                book.Chapters.Add(new ChapterRecord { Title = currentTitle, FileName = fileName });
                body.Clear();
                if (book.Chapters.Count % 50 == 0) progress?.Report($"已识别 {book.Chapters.Count} 个章节…");
            }
        }
        catch
        {
            Directory.Delete(bookPath, true);
            throw;
        }
    }

    public static Encoding GetEncoding(string path, ImportOptions? options)
    {
        if (options?.EncodingName is null or "自动") return DetectEncoding(path);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(options.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static Encoding DetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> bom = stackalloc byte[4];
        int read = stream.Read(bom);
        if (read >= 3 && bom[..3].SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })) return new UTF8Encoding(true);
        if (read >= 2 && bom[..2].SequenceEqual(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode;
        if (read >= 2 && bom[..2].SequenceEqual(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode;
        stream.Position = 0;
        try
        {
            using var check = new StreamReader(stream, new UTF8Encoding(false, true), false, 8192, true);
            while (check.ReadLine() != null) { }
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030");
        }
    }
}

public static class BookSearch
{
    public static List<SearchHit> Search(BookRecord book, string query, int maximum = 300, CancellationToken token = default)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        for (int chapter = 0; chapter < book.Chapters.Count; chapter++)
        {
            token.ThrowIfCancellationRequested();
            string content = LibraryStore.ReadChapter(book, chapter).Replace("\r\n", "\n").Replace('\r', '\n');
            int start = 0;
            while (start < content.Length)
            {
                int index = content.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                int left = Math.Max(0, index - 18);
                int right = Math.Min(content.Length, index + query.Length + 25);
                string excerpt = content[left..right].Replace('\r', ' ').Replace('\n', ' ').Trim();
                token.ThrowIfCancellationRequested();
                hits.Add(new SearchHit { BookId = book.Id, Chapter = chapter, Offset = index, Display = $"{book.Chapters[chapter].Title}  ·  {excerpt}" });
                if (hits.Count >= maximum) return hits;
                start = index + Math.Max(1, query.Length);
            }
        }
        return hits;
    }
}
