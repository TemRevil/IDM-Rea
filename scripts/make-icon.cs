#:project ../src/IDMRea.Contracts/IDMRea.Contracts.csproj
#:property PublishAot=false
// Writes the exe's own icon file, which Windows shows before the app runs (Explorer, a pinned taskbar icon).
// Once running, the app and the engine draw the same icon in the Windows accent colour (IconArt.Files).
// Run: dotnet run scripts/make-icon.cs
using IDMRea.Contracts;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "scripts", ".."));
var target = Path.Combine(root, "src", "IDMRea.App", "Assets", "AppIcon.ico");
File.WriteAllBytes(target, IconArt.Ico(IconArt.ExeRing));
Console.WriteLine($"Wrote {target} ({string.Join(", ", IconArt.IcoSizes)})");
