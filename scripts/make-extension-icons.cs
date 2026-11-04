#:project ../src/IDMRea.Contracts/IDMRea.Contracts.csproj
#:property PublishAot=false
// Writes the browser extension's icons: the app's ring on its charcoal tile, in the exe's neutral grey.
// Run: dotnet run scripts/make-extension-icons.cs
using IDMRea.Contracts;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "scripts", ".."));
var folder = Path.Combine(root, "src", "IDMRea.Extension", "icons");
Directory.CreateDirectory(folder);
foreach (var size in new[] { 16, 32, 48, 128 })
{
    var path = Path.Combine(folder, $"icon-{size}.png");
    File.WriteAllBytes(path, IconArt.Png(size, IconArt.Rgba(size, IconArt.ExeRing)));
    Console.WriteLine($"Wrote {path}");
}
