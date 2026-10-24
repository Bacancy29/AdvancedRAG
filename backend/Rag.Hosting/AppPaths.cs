namespace Rag.Hosting;

/// <summary>Resolves relative paths (models, eval sets, uploads) against the backend folder, wherever the app runs from.</summary>
public static class AppPaths
{
    public static string Root { get; } = FindRoot();

    public static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AdvancedRag.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
