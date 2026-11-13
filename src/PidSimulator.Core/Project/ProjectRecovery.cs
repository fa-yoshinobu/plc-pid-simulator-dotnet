namespace PidSimulator.Core.Project;

public static class ProjectRecovery
{
    /// <summary>元ファイルとDEMOを上書きせず、回復用の新しいファイルへ保存する。</summary>
    public static string Save(ProjectDocument document, string? originalPath, string fallbackDirectory,
        Action<Exception>? reportFailure = null)
    {
        string name = Path.GetFileNameWithoutExtension(originalPath) ?? document.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "project";
        if (name.Length > 100) name = name[..100];
        string filename = $"{name}.{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.recovered.psim";
        if (!string.IsNullOrWhiteSpace(originalPath))
        {
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(originalPath))!;
                string path = Path.Combine(directory, filename);
                ProjectSerializer.Save(path, document);
                return StoragePath.ResolveExisting(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only project location must not prevent recovery into the user profile.
                if (reportFailure != null) reportFailure(ex);
                else System.Diagnostics.Trace.WriteLine(ex);
            }
        }
        Directory.CreateDirectory(fallbackDirectory);
        string fallback = Path.Combine(fallbackDirectory, filename);
        ProjectSerializer.Save(fallback, document);
        return StoragePath.ResolveExisting(fallback);
    }
}
