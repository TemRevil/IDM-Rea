namespace IDMRea.Core;

/// <summary>
/// The "Mark of the Web": a Zone.Identifier stream saying the file came from the internet, so Windows and other
/// apps warn before running it. Windows only; drives without alternate streams (FAT, exFAT) simply get no mark.
/// </summary>
internal static class ZoneMark
{
    public static void Write(string path, string url)
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri : url;
        try { File.WriteAllText(path + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={host}\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
}
