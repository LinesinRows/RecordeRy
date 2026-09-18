namespace RecordeRy.Core.Replay;

public sealed class SegmentManager
{
    private readonly string _directory;

    public SegmentManager(string directory)
    {
        _directory = directory;
    }

    public IReadOnlyList<string> GetSegments()
    {
        if (!Directory.Exists(_directory))
            return [];

        return Directory
            .GetFiles(
                _directory,
                "segment_*.mp4")
            .OrderBy(
                File.GetLastWriteTimeUtc)
            .ToList();
    }
}