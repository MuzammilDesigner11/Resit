#:property PublishAot=false
// Packs the Linux build into resit-linux-x64.tar.gz with the right file modes, so the
// archive works when built on Windows too (NTFS has no executable bit).
//
// Usage: dotnet run tools/pack-linux.cs <folder> <archive.tar.gz>
using System.Formats.Tar;
using System.IO.Compression;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/pack-linux.cs <folder> <archive.tar.gz>");
    return 1;
}
var (folder, archive) = (Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
string[] executables = ["Resit", "install.sh", "setup.sh"];
const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
const UnixFileMode Regular = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

await using var output = File.Create(archive);
await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
await using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
foreach (var file in Directory.EnumerateFiles(folder).Order())
{
    var name = Path.GetFileName(file);
    if (name.EndsWith(".pdb") || name.EndsWith(".tar.gz")) continue;
    var entry = new PaxTarEntry(TarEntryType.RegularFile, $"resit/{name}")
    {
        Mode = executables.Contains(name) ? Executable : Regular,
        ModificationTime = File.GetLastWriteTimeUtc(file),
        DataStream = File.OpenRead(file),
    };
    await tar.WriteEntryAsync(entry);
    await entry.DataStream.DisposeAsync();
    Console.WriteLine($"  {entry.Mode,-60} {name}");
}
return 0;
