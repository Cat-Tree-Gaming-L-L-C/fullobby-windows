namespace ChllSeeding.Core.Config;

/// <summary>
/// Crash-safe file writes: write to a per-write temp file, flush to
/// disk, then atomically rename over the target so an interrupted write
/// (disk full, crash, power loss) never leaves a truncated file.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string target, byte[] content)
    {
        var parent = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("target path has no parent directory", nameof(target));

        // Unique per write (target name + GUID): a process-id-only name collided when two saves ran
        // concurrently — the second would hit the first's FileShare.None handle and throw, dropping
        // a save. The GUID also keeps writes to different files in the same directory from clashing.
        var fileName = Path.GetFileName(target);
        var tmpPath = Path.Combine(parent, $"{fileName}.{Guid.NewGuid():N}.tmp");

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
