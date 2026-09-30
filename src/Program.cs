using PocketReader;

namespace PocketReader;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--self-test")
        {
            var book = TxtImporter.Import(args[1], Path.Combine(Path.GetTempPath(), "Read_meSelfTest"));
            Console.WriteLine($"chapters={book.Chapters.Count};first={book.Chapters[0].Title};last={book.Chapters[^1].Title}");
            Console.WriteLine($"chapter448={book.Chapters.FirstOrDefault(x => x.Title.Contains("448"))?.Title}");
            return;
        }
        ApplicationConfiguration.Initialize();
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant())));
        using var instance = new Mutex(true, "Local\\Read_me_" + identity, out bool firstInstance);
        if (!firstInstance) { MessageBox.Show("Read_me 已在此文件夹运行，请从任务栏或托盘打开现有窗口。", "Read_me"); return; }
        try { Application.Run(new ReaderForm()); }
        catch (Exception ex) { MessageBox.Show("Read_me 无法启动。请保留 ReaderData 文件夹。\n" + ex.Message, "Read_me", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

