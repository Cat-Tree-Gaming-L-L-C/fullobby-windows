namespace ChllSeeding.Core.Config;

/// <summary>
/// Crash-safe file writes. Port of <c>write_file_safe</c> in
/// <c>src-rust/src/config.rs</c>: write to a per-process temp file, flush to
/// disk, then atomically rename over the target so an interrupted write
/// (disk full, crash, power loss) never leaves a truncated file.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string target, byte[] content)
    {
        var parent = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("target path has no parent directory", nameof(target));

        var tmpPath = Path.Combine(parent, $"config.json.tmp_{Environment.ProcessId}");

        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(content, 0, content.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* best effort cleanup */ }
            throw;
        }
    }
}
