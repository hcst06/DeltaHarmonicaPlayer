using System.Text;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace DeltaHarmonicaPlayer;

internal sealed record SongNote(long StartUs, long EndUs, int Pitch, int Velocity);

internal sealed record SongTrack(
    int Index,
    int SourceTrack,
    int Channel,
    string Name,
    IReadOnlyList<SongNote> Notes,
    bool IsPercussion,
    double MelodyScore)
{
    public override string ToString() => $"轨道 {SourceTrack + 1} / 通道 {Channel + 1}　{Name}　·　{Notes.Count} 个音符";
}

internal sealed record LoadedSong(string Path, string Title, IReadOnlyList<SongTrack> Tracks);

internal static class MidiLibrary
{
    private static readonly Encoding TextEncoding = RegisterCodePages();

    public static string SongsDirectory
    {
        get
        {
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Directory.GetParent(appDir)?.FullName ?? appDir;
            return Path.Combine(root, "Songs");
        }
    }

    public static IReadOnlyList<string> FindSongs()
    {
        Directory.CreateDirectory(SongsDirectory);
        return Directory.EnumerateFiles(SongsDirectory)
            .Where(IsMidi)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static string Import(string sourcePath)
    {
        if (!File.Exists(sourcePath) || !IsMidi(sourcePath))
        {
            throw new InvalidDataException("请选择 .mid 或 .midi 文件。");
        }

        Directory.CreateDirectory(SongsDirectory);
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var destination = Path.Combine(SongsDirectory, stem + extension);
        var index = 2;
        while (File.Exists(destination) && !FilesEqual(sourcePath, destination))
        {
            destination = Path.Combine(SongsDirectory, $"{stem} ({index++}){extension}");
        }

        if (!File.Exists(destination))
        {
            File.Copy(sourcePath, destination);
        }

        return destination;
    }

    public static string CreateUniqueSongPath(string title)
    {
        Directory.CreateDirectory(SongsDirectory);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safeTitle = new string(title.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            safeTitle = "新简谱";
        }
        if (safeTitle.Length > 80)
        {
            safeTitle = safeTitle[..80].TrimEnd(' ', '.');
        }
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };
        if (reservedNames.Contains(safeTitle)) safeTitle = "_" + safeTitle;

        var destination = Path.Combine(SongsDirectory, safeTitle + ".mid");
        var index = 2;
        while (File.Exists(destination))
        {
            destination = Path.Combine(SongsDirectory, $"{safeTitle} ({index++}).mid");
        }
        return destination;
    }

    public static LoadedSong Load(string path)
    {
        var settings = new ReadingSettings
        {
            TextEncoding = TextEncoding,
            NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
            InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
            InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits,
            InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
            MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
            UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Ignore,
            UnknownChunkIdPolicy = UnknownChunkIdPolicy.Skip,
            UnknownChannelEventPolicy = UnknownChannelEventPolicy.SkipStatusByteAndTwoDataBytes,
            SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOff,
        };

        var midi = MidiFile.Read(path, settings);
        var tempoMap = midi.GetTempoMap();
        var chunks = midi.GetTrackChunks().ToList();
        var tracks = new List<SongTrack>();

        for (var i = 0; i < chunks.Count; i++)
        {
            var channelGroups = chunks[i].GetNotes()
                .Where(n => (int)n.Channel != 9 && n.Length > 0)
                .GroupBy(n => (int)n.Channel)
                .OrderBy(g => g.Key)
                .ToArray();

            if (channelGroups.Length == 0)
            {
                continue;
            }

            var sourceName = chunks[i].Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                sourceName = "未命名";
            }

            foreach (var channelGroup in channelGroups)
            {
                var notes = channelGroup
                    .Select(n => new SongNote(
                        (long)n.TimeAs<MetricTimeSpan>(tempoMap).TotalMicroseconds,
                        (long)n.EndTimeAs<MetricTimeSpan>(tempoMap).TotalMicroseconds,
                        n.NoteNumber,
                        n.Velocity))
                    .OrderBy(n => n.StartUs)
                    .ThenByDescending(n => n.Pitch)
                    .ToArray();

                var overlapRatio = CalculateOverlapRatio(notes);
                var meanPitch = notes.Average(n => n.Pitch);
                var durationSeconds = Math.Max(1.0, (notes.Max(n => n.EndUs) - notes.Min(n => n.StartUs)) / 1_000_000.0);
                var density = Math.Min(1.0, notes.Length / durationSeconds / 8.0);
                var nameBoost = MelodyNameBoost(sourceName);
                var score = meanPitch + density * 12.0 - overlapRatio * 35.0 + nameBoost;
                tracks.Add(new SongTrack(tracks.Count, i, channelGroup.Key, sourceName, notes, false, score));
            }
        }

        if (tracks.Count == 0)
        {
            throw new InvalidDataException("这个 MIDI 中没有找到可演奏的旋律音符。");
        }

        return new LoadedSong(path, Path.GetFileNameWithoutExtension(path), tracks);
    }

    public static SongTrack PickDefaultTrack(LoadedSong song) =>
        song.Tracks.OrderByDescending(t => t.MelodyScore).ThenByDescending(t => t.Notes.Count).First();

    private static double CalculateOverlapRatio(IReadOnlyList<SongNote> notes)
    {
        if (notes.Count < 2)
        {
            return 0;
        }

        var overlaps = 0;
        long previousEnd = notes[0].EndUs;
        for (var i = 1; i < notes.Count; i++)
        {
            if (notes[i].StartUs < previousEnd)
            {
                overlaps++;
            }
            previousEnd = Math.Max(previousEnd, notes[i].EndUs);
        }
        return overlaps / (double)(notes.Count - 1);
    }

    private static double MelodyNameBoost(string name)
    {
        var normalized = name.ToLowerInvariant();
        if (new[] { "旋律", "主旋律", "melody", "lead", "vocal", "voice", "口琴", "flute" }
            .Any(normalized.Contains))
        {
            return 35.0;
        }

        if (new[] { "drum", "鼓", "bass", "贝斯", "低音", "chord", "和弦", "伴奏" }
            .Any(normalized.Contains))
        {
            return -30.0;
        }

        return 0.0;
    }

    private static bool FilesEqual(string left, string right)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);
        if (a.Length != b.Length)
        {
            return false;
        }
        return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    private static bool IsMidi(string path) =>
        Path.GetExtension(path).Equals(".mid", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".midi", StringComparison.OrdinalIgnoreCase);

    private static Encoding RegisterCodePages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030");
    }
}
