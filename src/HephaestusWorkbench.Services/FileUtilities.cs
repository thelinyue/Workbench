namespace HephaestusWorkbench.Services;

internal static class FileUtilities
{
    public static string RemoveAllExtensions(string fileName)
    {
        var current = fileName;
        while (!string.IsNullOrEmpty(Path.GetExtension(current))) current = Path.GetFileNameWithoutExtension(current);
        return current;
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    }

    public static long GetFileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
