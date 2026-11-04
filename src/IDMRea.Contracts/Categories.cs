namespace IDMRea.Contracts;

public enum Category { Video, Music, Documents, Programs, Archives, Images, Other }

public static class Categories
{
    private static readonly Dictionary<string, Category> Extensions = Build();

    private static Dictionary<string, Category> Build()
    {
        var map = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        void Add(Category c, string list) { foreach (var e in list.Split(' ')) map["." + e] = c; }
        Add(Category.Video, "mp4 mkv avi mov webm wmv flv m4v mpg mpeg 3gp ts");
        Add(Category.Music, "mp3 flac wav aac ogg m4a wma opus");
        Add(Category.Documents, "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp csv epub md");
        Add(Category.Programs, "exe msi msix msixbundle appx appxbundle bat cmd ps1 apk dmg");
        Add(Category.Archives, "zip rar 7z tar gz tgz bz2 xz iso cab zst");
        Add(Category.Images, "jpg jpeg png gif webp bmp svg tiff heic ico");
        return map;
    }

    private static readonly Dictionary<string, string> TypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip", ["application/x-zip-compressed"] = ".zip", ["application/pdf"] = ".pdf",
        ["application/x-7z-compressed"] = ".7z", ["application/vnd.rar"] = ".rar", ["application/x-rar-compressed"] = ".rar",
        ["application/gzip"] = ".gz", ["application/x-tar"] = ".tar", ["application/x-msdownload"] = ".exe",
        ["application/x-msi"] = ".msi", ["application/vnd.android.package-archive"] = ".apk",
        ["application/msword"] = ".doc", ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls", ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/epub+zip"] = ".epub", ["text/plain"] = ".txt", ["text/csv"] = ".csv",
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/x-matroska"] = ".mkv", ["video/quicktime"] = ".mov",
        ["audio/mpeg"] = ".mp3", ["audio/flac"] = ".flac", ["audio/wav"] = ".wav", ["audio/ogg"] = ".ogg", ["audio/mp4"] = ".m4a",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif", ["image/webp"] = ".webp", ["image/svg+xml"] = ".svg",
    };

    private static string? Bare(string? contentType) => contentType?.Split(';')[0].Trim();

    public static string ExtensionFor(string? contentType) =>
        Bare(contentType) is { } type && TypeExtensions.TryGetValue(type, out var ext) ? ext : "";

    public static Category From(string fileName, string? contentType)
    {
        if (Extensions.TryGetValue(Path.GetExtension(fileName), out var byName)) return byName;
        var type = Bare(contentType);
        if (string.IsNullOrEmpty(type)) return Category.Other;
        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return Category.Video;
        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return Category.Music;
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Category.Images;
        return Extensions.TryGetValue(ExtensionFor(type), out var byType) ? byType : Category.Other;
    }
}
