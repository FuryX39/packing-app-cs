using System.IO;
using System.IO.Compression;

namespace WarehousePacking.Services;

public static class LabelCache
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "cache", "fbs_labels");

    public static string LinePath(int jobId, int lineId) =>
        Path.Combine(Root, jobId.ToString(), $"{lineId}.pdf");

    public static bool HasLine(int jobId, int lineId)
    {
        var path = LinePath(jobId, lineId);
        return File.Exists(path) && new FileInfo(path).Length > 4;
    }

    public static byte[]? GetLine(int jobId, int lineId)
    {
        var path = LinePath(jobId, lineId);
        if (!File.Exists(path))
            return null;
        var data = File.ReadAllBytes(path);
        return data.Length >= 4 && data[0] == (byte)'%' && data[1] == (byte)'P' ? data : null;
    }

    public static void PutLine(int jobId, int lineId, byte[] data)
    {
        if (data is null || data.Length < 4 || data[0] != (byte)'%')
            return;
        var path = LinePath(jobId, lineId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    public static List<int> JobLineIds(JsonMap? job)
    {
        var ids = new List<int>();
        if (job is null) return ids;
        foreach (var line in job.Arr("lines"))
        {
            var id = line.IntOrNull("id");
            if (id is int n)
                ids.Add(n);
        }
        return ids;
    }

    public static (int Have, int Total) CachedCount(int jobId, IEnumerable<int> lineIds)
    {
        var ids = lineIds.ToList();
        return (ids.Count(id => HasLine(jobId, id)), ids.Count);
    }

    public static bool JobReady(JsonMap? job)
    {
        if (job is null) return false;
        var id = job.IntOrNull("id");
        if (id is null) return false;
        var ids = JobLineIds(job);
        return ids.Count > 0 && ids.All(lineId => HasLine(id.Value, lineId));
    }

    public static int SaveZip(int jobId, byte[] zipBytes)
    {
        var saved = 0;
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            var name = Path.GetFileName(entry.FullName);
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!int.TryParse(Path.GetFileNameWithoutExtension(name), out var lineId))
                continue;
            using var stream = entry.Open();
            using var buf = new MemoryStream();
            stream.CopyTo(buf);
            PutLine(jobId, lineId, buf.ToArray());
            saved++;
        }
        return saved;
    }

    public static string FormatSize(long n)
    {
        if (n < 1024) return $"{n} Б";
        if (n < 1024 * 1024) return $"{n / 1024.0:0.0} КБ";
        return $"{n / (1024.0 * 1024):0.0} МБ";
    }

    public static List<int> CachedJobIds()
    {
        if (!Directory.Exists(Root))
            return [];
        return Directory.GetDirectories(Root)
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name, out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .OrderBy(x => x)
            .ToList();
    }

    public static (int Jobs, int Files, long Bytes) Summary()
    {
        var jobs = CachedJobIds();
        var files = 0;
        long size = 0;
        if (Directory.Exists(Root))
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*.pdf", SearchOption.AllDirectories))
            {
                files++;
                try { size += new FileInfo(file).Length; } catch { }
            }
        }
        return (jobs.Count, files, size);
    }

    public static (int Jobs, long Bytes) ClearExcept(ISet<int> keepIds)
    {
        var toClear = CachedJobIds().Where(id => !keepIds.Contains(id)).ToList();
        var removedJobs = 0;
        long removedBytes = 0;
        foreach (var jobId in toClear)
        {
            var path = Path.Combine(Root, jobId.ToString());
            if (!Directory.Exists(path))
                continue;
            long size = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    size += new FileInfo(file).Length;
            }
            catch { }
            try { Directory.Delete(path, true); } catch { }
            if (!Directory.Exists(path))
            {
                removedJobs++;
                removedBytes += size;
            }
        }
        return (removedJobs, removedBytes);
    }
}
