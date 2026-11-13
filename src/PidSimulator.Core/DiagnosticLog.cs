using System.Diagnostics;
using System.Text;

namespace PidSimulator.Core;

/// <summary>診断記録をサイズ上限付きで保持する。記録の失敗で演算を止めない。</summary>
public sealed class DiagnosticLog(string directory, int maxFileBytes = 256 * 1024, int maxFiles = 4)
{
    public static DiagnosticLog Shared { get; } = new(DefaultDirectory);

    private readonly object _gate = new();
    public string DirectoryPath { get; } = directory;
    public string GetExistingDirectoryPath()
    {
        Directory.CreateDirectory(DirectoryPath);
        return StoragePath.ResolveExisting(DirectoryPath);
    }
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PidSimulator", "logs");

    public void Write(string context, Exception? error = null)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string message = $"{DateTimeOffset.Now:O} {context}{(error == null ? "" : Environment.NewLine + error)}";
                // A pathological exception must not exceed the same bound as an ordinary entry.
                if (message.Length > maxFileBytes / 4) message = message[..(maxFileBytes / 4)];
                message += Environment.NewLine;
                string current = Path.Combine(DirectoryPath, "pid.log");
                if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(message) > maxFileBytes)
                {
                    for (int i = maxFiles - 1; i >= 1; i--)
                    {
                        string source = Path.Combine(DirectoryPath, i == 1 ? "pid.log" : $"pid.{i - 1}.log");
                        if (File.Exists(source)) File.Move(source, Path.Combine(DirectoryPath, $"pid.{i}.log"), overwrite: true);
                    }
                }
                File.AppendAllText(current, message, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"診断ログを保存できません：{ex}");
            }
        }
    }
}
