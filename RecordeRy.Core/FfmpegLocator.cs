namespace RecordeRy.Core;

public static class FfmpegLocator
{
    private const int MaxAncestorLevels = 8;

    public static string Resolve(string baseDirectory)
    {
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDirectory, "tools", "ffmpeg", "ffmpeg.exe")
        };

        var directory = new DirectoryInfo(baseDirectory);

        for (var i = 0; i < MaxAncestorLevels && directory != null; i++)
        {
            candidates.Add(
                Path.Combine(directory.FullName, "tools", "ffmpeg", "ffmpeg.exe"));

            directory = directory.Parent;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            "ffmpeg.exe bulunamadı. Aranan konumlar:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, candidates.Distinct()));
    }
}
