namespace RecordeRy.Core.Replay;

public sealed class SegmentManager
{
    private readonly string _directory;

    public SegmentManager(string directory)
    {
        _directory = directory;
    }

    /// <summary>
    /// Segmentleri kayıt sırasına göre (dosya adındaki numaraya göre)
    /// döndürür. Son değiştirilme zamanına göre sıralanamaz: Windows açık
    /// dosyanın zamanını kapanana kadar güncellemediği için yazılmakta olan
    /// segment listenin ortasına düşüyordu.
    /// </summary>
    public IReadOnlyList<string> GetSegments()
    {
        if (!Directory.Exists(_directory))
            return [];

        return Directory
            .GetFiles(
                _directory,
                "segment_*.mp4")
            .OrderBy(GetIndex)
            .ToList();
    }

    private static long GetIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        return long.TryParse(
            name.AsSpan("segment_".Length),
            out var index)
            ? index
            : long.MaxValue;
    }
}
