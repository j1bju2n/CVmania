namespace CVmania.Core.Export;

/// <summary>
/// Keeps exported paths short enough for osu! stable. osu! is a .NET Framework 4.0 program: it lists
/// a folder fine but cannot open a file whose full path has 260 or more characters (directories:
/// 248). Such a beatmap ends up in osu!.db without metadata and breaks song select. osu! itself
/// shortens the folder name of a set so that its longest file still fits; we do the same.
/// </summary>
public static class ExportPaths
{
    /// <summary>Longest full file path osu! stable can open.</summary>
    public const int HardLimit = 259;
    /// <summary>Longest directory path osu! stable can open.</summary>
    public const int DirLimit = 247;
    /// <summary>Target for new folders: leaves room for osu! renaming the .osu when the difficulty name or creator changes.</summary>
    public const int SoftLimit = 235;
    public const int MinFolderName = 24;

    private static int FullLength(string directory) => Path.GetFullPath(directory).TrimEnd('\\', '/').Length;

    private static string Clean(string name) => name.Trim().TrimEnd('.', ' ');

    /// <summary>
    /// Folder name "Artist - Title{suffix}" for a new folder inside <paramref name="parentDirectory"/>,
    /// shortened so that a file name of <paramref name="longestFileNameLength"/> characters inside it
    /// stays within <see cref="SoftLimit"/>. When it does not fit, the artist goes first (the title says
    /// more about which song it is), then the title is cut; the suffix always survives.
    /// </summary>
    public static string FitFolderName(string parentDirectory, string artist, string title, string suffix, int longestFileNameLength)
    {
        artist = artist.Trim();
        title = title.Trim();
        var baseName = artist.Length > 0 && title.Length > 0 ? artist + " - " + title : artist + title;
        if (baseName.Length == 0) baseName = "untitled";
        var full = Clean(baseName + suffix);
        int parent = FullLength(parentDirectory);
        int max = SoftLimit - parent - 2 - longestFileNameLength;
        max = Math.Max(max, MinFolderName);
        max = Math.Min(max, DirLimit - parent - 1);
        if (max < 1) throw new PathTooLongException($"'{parentDirectory}' is too long for osu! (folders must stay under {DirLimit + 1} characters).");
        if (full.Length <= max) return full;

        var lead = title.Length > 0 ? title : baseName;
        var titled = Clean(lead + suffix);
        if (titled.Length <= max) return titled;

        int keep = max - suffix.TrimEnd().Length;
        if (keep < 8) return Clean(titled[..max]);
        var head = lead[..Math.Min(lead.Length, keep)].TrimEnd(' ', '.', ',', '-', '(', '[');
        var name = Clean(head + suffix);
        return name.Length > 0 ? name : Clean(titled[..max]);
    }

    /// <summary>
    /// <paramref name="fileName"/> shortened so that it fits in <paramref name="directory"/>. For
    /// "Artist - Title (Creator) [Version].osu" the "(Creator) [Version]" tail is kept, because that is
    /// what tells difficulties apart; the artist/title part is cut.
    /// </summary>
    public static string FitFileName(string directory, string fileName, int limit = HardLimit)
    {
        int room = limit - FullLength(directory) - 1;
        if (fileName.Length <= room) return fileName;

        var ext = Path.GetExtension(fileName);
        var stem = fileName[..^ext.Length];
        int stemRoom = room - ext.Length;
        if (stemRoom < 1)
            throw new PathTooLongException($"'{directory}' leaves no room for a file name; osu! cannot open paths of {HardLimit + 1} or more characters.");

        int bracket = stem.LastIndexOf(" [", StringComparison.Ordinal);
        int paren = bracket > 0 ? stem.LastIndexOf(" (", bracket, StringComparison.Ordinal) : -1;
        if (paren > 0)
        {
            var tail = stem[paren..];
            int headRoom = stemRoom - tail.Length;
            if (headRoom >= 8)
            {
                var head = stem[..Math.Min(paren, headRoom)].TrimEnd(' ', '.', ',', '-');
                if (head.Length > 0) return head + tail + ext;
            }
        }
        var cut = stem[..stemRoom].TrimEnd(' ', '.');
        return (cut.Length > 0 ? cut : stem[..stemRoom]) + ext;
    }

    /// <summary>Throws when the directory itself is already too long for osu!.</summary>
    public static void EnsureDirectoryFits(string directory)
    {
        int len = FullLength(directory);
        if (len > DirLimit)
            throw new PathTooLongException($"The output folder path is {len} characters long. osu! cannot open folders of {DirLimit + 1} or more characters; choose a shorter folder.");
    }
}
