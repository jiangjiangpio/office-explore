using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Ink;
using OfficeTouchBar.Models;

namespace OfficeTouchBar.Infrastructure;

internal sealed class InkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly object _gate = new();
    internal bool DeletePreviousDays { get; set; }
    internal Func<DateOnly> Today { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    internal InkStore(string? root = null) => _root = root ?? Path.Combine(AppSettings.DirectoryPath, "Ink");

    internal InkPageData Load(InkPageKey key)
    {
        lock (_gate) return LoadCore(key);
    }

    private InkPageData LoadCore(InkPageKey key)
    {
        var path = PagePath(key);
        try
        {
            if (!File.Exists(path)) return InkPageData.Empty();
            var page = JsonSerializer.Deserialize<StoredInkPage>(File.ReadAllText(path), JsonOptions);
            if (page is null
                || page.SlideNumber != key.SlideNumber
                || !string.Equals(page.PresentationIdentity, key.PresentationIdentity, StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Write($"ink metadata mismatch: {path}");
                return InkPageData.Empty();
            }

            var day = DayOf(page);
            if (DeletePreviousDays && day < Today())
            {
                File.Delete(path);
                return InkPageData.Empty();
            }
            using var stream = new MemoryStream(Convert.FromBase64String(page.InkData));
            return new InkPageData(new StrokeCollection(stream), page.CanvasWidth, page.CanvasHeight) { InkDay = day };
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"ink load failed path={path}: {ex}");
            return InkPageData.Empty();
        }
    }

    internal void Save(InkPageKey key, InkPageData data)
    {
        lock (_gate) SaveCore(key, data);
    }

    private void SaveCore(InkPageKey key, InkPageData data)
    {
        var path = PagePath(key);
        try
        {
            var day = data.InkDay ?? Today();
            // A queued save from yesterday must never recreate an expired file,
            // nor delete a newer file already written to the same page path.
            if (DeletePreviousDays && day < Today()) return;
            if (data.Strokes.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new MemoryStream();
            data.Strokes.Save(stream, true);
            var page = new StoredInkPage
            {
                FormatVersion = 2,
                InkDay = day,
                PresentationIdentity = key.PresentationIdentity,
                PresentationName = key.PresentationName,
                SlideNumber = key.SlideNumber,
                TotalSlides = key.TotalSlides,
                CanvasWidth = data.CanvasWidth,
                CanvasHeight = data.CanvasHeight,
                SavedAtUtc = DateTime.UtcNow,
                InkData = Convert.ToBase64String(stream.ToArray())
            };
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(page, JsonOptions));
            File.Move(temporary, path, true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"ink save failed path={path}: {ex}");
            throw;
        }
    }

    internal string PagePath(InkPageKey key)
    {
        var normalized = key.PresentationIdentity.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return Path.Combine(_root, hash, $"slide-{key.SlideNumber:D5}.otbink");
    }

    internal bool HasPresentationInk(InkPageKey key) => PresentationFiles(key).Any();

    internal void DiscardPresentation(InkPageKey key)
    {
        lock (_gate)
            foreach (var file in PresentationFiles(key)) File.Delete(file);
    }

    internal int DeleteExpired()
    {
        lock (_gate)
        {
            if (!DeletePreviousDays || !Directory.Exists(_root)) return 0;
            var count = 0;
            // Only our page files in direct presentation folders; never follow
            // junctions into another location or delete unrelated files.
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "slide-*.otbink"))
                {
                    try
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        var page = JsonSerializer.Deserialize<StoredInkPage>(File.ReadAllText(path), JsonOptions);
                        if (page is null || DayOf(page) >= Today()) continue;
                        File.Delete(path);
                        count++;
                    }
                    catch (Exception ex) { DiagnosticLog.Write($"ink expiry failed path={path}: {ex.Message}"); }
                }
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            DiagnosticLog.Write($"ink expiry day={Today()} deleted={count}");
            return count;
        }
    }

    private static DateOnly DayOf(StoredInkPage page) => page.InkDay
        ?? DateOnly.FromDateTime(page.SavedAtUtc.ToLocalTime());

    private IEnumerable<string> PresentationFiles(InkPageKey key)
    {
        var directory = Path.GetDirectoryName(PagePath(key))!;
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "slide-*.otbink", SearchOption.TopDirectoryOnly)
            : [];
    }

    private sealed class StoredInkPage
    {
        public int FormatVersion { get; set; }
        public DateOnly? InkDay { get; set; }
        public string PresentationIdentity { get; set; } = "";
        public string PresentationName { get; set; } = "";
        public int SlideNumber { get; set; }
        public int TotalSlides { get; set; }
        public double CanvasWidth { get; set; }
        public double CanvasHeight { get; set; }
        public DateTime SavedAtUtc { get; set; }
        public string InkData { get; set; } = "";
    }
}
