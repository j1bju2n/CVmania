using CVmania.Core.Export;
using Xunit;

namespace CVmania.Core.Tests;

public class ExportPathsTests
{
    private const string Songs = @"C:\Users\sama\AppData\Local\osu!\Songs";

    [Fact]
    public void LongSongNames_AreShortenedLikeOsuDoes()
    {
        // the export that broke osu! on 2026-11-13: folder 105 + file 140 characters = a 285 character path
        const string artist = "ric3show3r feat. m4titann, Rio Momose, 4NK4K3";
        const string title = "The proximity of our souls making to something";
        const string baseName = artist + " - " + title;
        const string osu = "ric3show3r feat. m4titann, Rio Momose, 4NK4K3 - The proximity of our souls making to something (Cut Ver.) (Tamania) [Inertial alignment].osu";
        Assert.True(Songs.Length + 1 + (baseName + " (Cut Ver.)").Length + 1 + osu.Length > ExportPaths.HardLimit);

        var folder = ExportPaths.FitFolderName(Songs, artist, title, " (Cut Ver.)", osu.Length);
        Assert.EndsWith(" (Cut Ver.)", folder);
        Assert.Equal("The proximity of our souls making to somethi (Cut Ver.)", folder); // the artist goes first, then the title is cut
        // with a little more room the whole title stays
        Assert.Equal("The proximity of our souls making to something (Cut Ver.)", ExportPaths.FitFolderName(Songs, artist, title, " (Cut Ver.)", 130));
        var dir = Songs + "\\" + folder;
        Assert.True(dir.Length + 1 + osu.Length <= ExportPaths.SoftLimit, $"{dir.Length + 1 + osu.Length}");
        Assert.Equal(osu, ExportPaths.FitFileName(dir, osu)); // the canonical .osu name survives, as in osu!'s own folders
    }

    [Fact]
    public void ShortNames_AreLeftAlone()
    {
        Assert.Equal("Gardens - Twin Bloom (Cut Ver.)", ExportPaths.FitFolderName(Songs, "Gardens", "Twin Bloom", " (Cut Ver.)", 60));
        Assert.Equal("audio.mp3", ExportPaths.FitFileName(Songs + @"\Gardens - Twin Bloom (Cut Ver.)", "audio.mp3"));
        ExportPaths.EnsureDirectoryFits(Songs + @"\Gardens - Twin Bloom (Cut Ver.)");
    }

    [Fact]
    public void FileNames_KeepCreatorAndVersionWhenTheyMustBeCut()
    {
        var dir = Songs + "\\" + new string('d', 150); // 189 characters: only 69 left for the file
        var name = new string('A', 60) + " - " + new string('T', 60) + " (Tamania) [Hard].osu";
        var fit = ExportPaths.FitFileName(dir, name);
        Assert.True(dir.Length + 1 + fit.Length <= ExportPaths.HardLimit);
        Assert.EndsWith(" (Tamania) [Hard].osu", fit);
        Assert.StartsWith("AAAA", fit);

        // names never end in a space or a dot (Windows strips those silently)
        var folder = ExportPaths.FitFolderName(Songs, "", new string('x', 30) + ". . . " + new string('y', 200), "", 162); // cut lands inside ". . ."
        Assert.Equal(new string('x', 30), folder);
    }

    [Fact]
    public void OverlongDirectories_AreRefused()
    {
        var dir = @"C:\" + new string('d', 250);
        Assert.Throws<PathTooLongException>(() => ExportPaths.EnsureDirectoryFits(dir));
        Assert.Throws<PathTooLongException>(() => ExportPaths.FitFileName(@"C:\" + new string('d', 256), "audio.mp3"));
    }
}
