using CVmania.Core.Audio;
using CVmania.Core.Cut;
using CVmania.Core.Osu;

namespace CVmania.Core.Export;

public enum AudioFormat { Mp3, Ogg, Wav }

public sealed class ExportOptions
{
    public AudioFormat Format { get; set; } = AudioFormat.Mp3;
    /// <summary>MP3 CBR bitrate in kbps (ranking criteria allow up to 192).</summary>
    public int Mp3BitrateKbps { get; set; } = 192;
    /// <summary>Vorbis quality 0..1 (0.6 is roughly 192 kbps).</summary>
    public float OggQuality { get; set; } = 0.6f;
    /// <summary>Base name of the audio file without extension.</summary>
    public string AudioBaseName { get; set; } = "audio";
    /// <summary>Directory that will receive the audio and the .osu.</summary>
    public required string OutputDirectory { get; set; }
    public bool WriteBeatmap { get; set; } = true;
    /// <summary>Copy the background image (and nothing else) next to the new beatmap.</summary>
    public bool CopyBackground { get; set; } = true;
    /// <summary>Decode the encoded file again and check it lines up with the rendered PCM.</summary>
    public bool VerifyOffset { get; set; } = true;
    public CutOptions Cut { get; set; } = new();
}

public sealed class ExportReport
{
    public required string AudioPath { get; init; }
    public string? BeatmapPath { get; init; }
    public CutResult? Beatmap { get; init; }
    public OffsetReport? Offset { get; init; }
    public List<string> Warnings { get; } = new();
    public double OutputLengthMs { get; init; }
}

/// <summary>Renders, encodes, verifies and writes the beatmap for a cut.</summary>
public static class CutExporter
{
    public static string ExtensionFor(AudioFormat f) => f switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Ogg => ".ogg",
        _ => ".wav",
    };

    public static ExportReport Export(OsuFile beatmap, PcmAudio sourceAudio, IReadOnlyList<CutRegion> regions, ExportOptions options,
        IProgress<string>? progress = null)
    {
        // osu! stable cannot open paths of 260+ characters: refuse an over-long folder, shorten file names that would not fit
        ExportPaths.EnsureDirectoryFits(options.OutputDirectory);
        Directory.CreateDirectory(options.OutputDirectory);
        var audioName = ExportPaths.FitFileName(options.OutputDirectory, options.AudioBaseName + ExtensionFor(options.Format));
        var audioPath = Path.Combine(options.OutputDirectory, audioName);
        var warnings = new List<string>();

        // the audio and the .osu must be built from exactly the same regions: clipped to the audio first
        var clipped = CutRegion.Normalize(regions, sourceAudio.DurationMs);
        if (clipped.Count == 0) throw new ArgumentException("No region lies inside the audio.", nameof(regions));
        var unclipped = CutRegion.Normalize(regions);
        if (unclipped.Count != clipped.Count || unclipped.Zip(clipped).Any(p => p.First.StartMs != p.Second.StartMs || p.First.EndMs != p.Second.EndMs))
            warnings.Add($"Some regions reached outside the audio (0 - {sourceAudio.DurationMs:0} ms) and were clipped to it.");

        progress?.Report("Rendering audio...");
        var rendered = CutRenderer.Render(sourceAudio, clipped, options.Cut);

        progress?.Report($"Encoding {audioName}...");
        var offset = EncodeVerified(rendered, audioPath, options, progress);

        CutResult? cut = null;
        string? beatmapPath = null;
        if (options.WriteBeatmap)
        {
            progress?.Report("Writing beatmap...");
            cut = CutPlanner.Transform(beatmap, clipped, options.Cut, audioName);
            warnings.AddRange(cut.Warnings);

            string? bgFrom = null, bgTo = null;
            if (options.CopyBackground && beatmap.SourcePath != null)
            {
                var bg = FindBackground(beatmap);
                if (bg != null)
                {
                    bgFrom = Path.Combine(Path.GetDirectoryName(beatmap.SourcePath)!, bg);
                    bgTo = Path.Combine(options.OutputDirectory, bg);
                    if (Path.GetFullPath(bgTo).Length > ExportPaths.HardLimit)
                    {
                        // keep the image reachable for osu!: copy it under a short name and point the .osu at it
                        var shortName = "background" + Path.GetExtension(bg);
                        if (ReplaceBackground(cut.Output, bg, shortName))
                        {
                            bgTo = Path.Combine(options.OutputDirectory, shortName);
                            warnings.Add($"The background file name was too long for osu! (path limit 260) and was copied as '{shortName}'.");
                        }
                    }
                }
            }

            var osuName = BuildOsuFileName(cut.Output);
            var fitted = ExportPaths.FitFileName(options.OutputDirectory, osuName);
            if (fitted != osuName)
                warnings.Add("The .osu file name was shortened so that osu! can open it (full paths must stay under 260 characters).");
            beatmapPath = Path.Combine(options.OutputDirectory, fitted);
            cut.Output.Save(beatmapPath);

            if (bgFrom != null && bgTo != null && File.Exists(bgFrom) &&
                !string.Equals(Path.GetFullPath(bgFrom), Path.GetFullPath(bgTo), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bgTo)!);
                File.Copy(bgFrom, bgTo, true);
            }
        }

        var report = new ExportReport
        {
            AudioPath = audioPath,
            BeatmapPath = beatmapPath,
            Beatmap = cut,
            Offset = offset,
            OutputLengthMs = rendered.DurationMs,
        };
        report.Warnings.AddRange(warnings);
        if (offset != null && !offset.IsAligned) report.Warnings.Add("Offset check: " + offset.Message);
        return report;
    }

    /// <summary>
    /// Encode, then decode the file again with BASS (what osu! will do) and compare against the
    /// rendered PCM. If the decoder reports a shift, re-encode once with compensating padding.
    /// </summary>
    private static OffsetReport? EncodeVerified(PcmAudio rendered, string audioPath, ExportOptions options, IProgress<string>? progress)
    {
        int correction = 0; // frames added (>0) or dropped (<0) at the start before encoding
        OffsetReport? report = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            switch (options.Format)
            {
                case AudioFormat.Mp3: Mp3Encoder.Encode(PadStart(rendered, correction), audioPath, options.Mp3BitrateKbps); break;
                case AudioFormat.Ogg: OggEncoder.Encode(rendered, audioPath, options.OggQuality, OggEncoder.DefaultPrePadFrames + correction); break;
                default: WavWriter.WritePcm16(audioPath, PadStart(rendered, correction)); break;
            }
            if (!options.VerifyOffset) return null;

            progress?.Report("Verifying offset with BASS...");
            var decoded = BassDecoder.DecodeFile(audioPath);
            report = OffsetVerifier.Verify(rendered, decoded);
            if (report.Correlation < 0.9 || Math.Abs(report.LagSamples) <= 1) return report;
            // decoded audio is early (negative lag => samples missing at the start) or late: compensate
            correction -= report.LagSamples;
            progress?.Report($"Decoder reported {report.LagMs:+0.00;-0.00} ms; re-encoding with correction...");
        }
        return report;
    }

    private static PcmAudio PadStart(PcmAudio pcm, int frames)
    {
        if (frames == 0) return pcm;
        int ch = pcm.Channels;
        if (frames > 0)
        {
            var s = new float[(pcm.Frames + frames) * ch];
            Array.Copy(pcm.Samples, 0, s, (long)frames * ch, pcm.Samples.Length);
            return new PcmAudio(s, pcm.SampleRate, ch);
        }
        long drop = Math.Min(pcm.Frames, -frames);
        var t = new float[(pcm.Frames - drop) * ch];
        Array.Copy(pcm.Samples, drop * ch, t, 0, t.Length);
        return new PcmAudio(t, pcm.SampleRate, ch);
    }

    /// <summary>"Artist - Title (Creator) [Version].osu" with invalid characters removed, as osu! does.</summary>
    public static string BuildOsuFileName(OsuFile f)
    {
        var name = $"{f.Artist} - {f.Title} ({f.Creator}) [{f.Version}].osu";
        return SanitizeFileName(name);
    }

    public static string SanitizeFileName(string name)
    {
        var s = ReplaceInvalid(name).Trim();
        return s.Length == 0 ? "untitled" : s;
    }

    /// <summary>Invalid file-name characters replaced by '_', spaces untouched (for pieces that are joined later).</summary>
    public static string ReplaceInvalid(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>Points the background event of <paramref name="f"/> at another file name. False when no such line exists.</summary>
    private static bool ReplaceBackground(OsuFile f, string oldName, string newName)
    {
        var sec = f.GetSection("Events");
        if (sec == null) return false;
        for (int i = 0; i < sec.Lines.Count; i++)
        {
            var parts = sec.Lines[i].Trim().Split(',');
            if (parts.Length >= 3 && (parts[0] == "0" || parts[0].Equals("Background", StringComparison.OrdinalIgnoreCase)) &&
                parts[2].Trim().Trim('"') == oldName)
            {
                sec.Lines[i] = sec.Lines[i].Replace(oldName, newName);
                return true;
            }
        }
        return false;
    }

    /// <summary>Background file name from [Events] (0,0,"bg.jpg",...), or null.</summary>
    public static string? FindBackground(OsuFile f)
    {
        var sec = f.GetSection("Events");
        if (sec == null) return null;
        foreach (var line in sec.Lines)
        {
            var t = line.Trim();
            if (t.StartsWith("//") || t.Length == 0) continue;
            var parts = t.Split(',');
            if (parts.Length >= 3 && (parts[0] == "0" || parts[0].Equals("Background", StringComparison.OrdinalIgnoreCase)))
                return parts[2].Trim().Trim('"');
        }
        return null;
    }
}
