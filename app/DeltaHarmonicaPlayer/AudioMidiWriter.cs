using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace DeltaHarmonicaPlayer;

internal sealed record AudioTranscriptionNote(
    double StartSeconds,
    double DurationSeconds,
    int Pitch,
    double Amplitude);

internal sealed record AudioTranscriptionRequest(
    string Type,
    string Title,
    string? SourceName,
    string? Mode,
    bool LimitedToGameRange,
    IReadOnlyList<AudioTranscriptionNote> Notes);

internal sealed record AudioMidiWriteResult(string Path, int DetectedNotes, int MelodyNotes);

internal static class AudioMidiWriter
{
    private const short TicksPerQuarterNote = 480;
    private const double TicksPerSecond = TicksPerQuarterNote * 2.0; // 120 BPM
    private const int MaximumAcceptedNotes = 200_000;
    private const double MaximumStartSeconds = 10 * 60;

    public static AudioMidiWriteResult WriteToSongs(AudioTranscriptionRequest request)
    {
        ValidateRequest(request);
        var destination = MidiLibrary.CreateUniqueSongPath(request.Title);
        return Write(request, destination);
    }

    internal static AudioMidiWriteResult Write(AudioTranscriptionRequest request, string destination)
    {
        ValidateRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var detected = Normalize(request.Notes);
        if (detected.Count == 0)
        {
            throw new InvalidDataException("没有可写入 MIDI 的有效音符。");
        }

        var melody = ExtractMelody(detected);
        WriteFile(destination, detected, melody);

        try
        {
            var loaded = MidiLibrary.Load(destination);
            if (loaded.Tracks.Count == 0 || loaded.Tracks.All(t => t.Notes.Count == 0))
            {
                throw new InvalidDataException("生成的 MIDI 中没有可演奏音符。");
            }
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }

        return new AudioMidiWriteResult(destination, detected.Count, melody.Count);
    }

    private static void ValidateRequest(AudioTranscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentNullException.ThrowIfNull(request.Notes);
        if (!string.Equals(request.Type, "complete", StringComparison.Ordinal)
            || request.Title.Trim().Length > 100)
        {
            throw new InvalidDataException("AI 识别结果的曲名或格式无效。");
        }
        if (request.Notes.Count > MaximumAcceptedNotes)
        {
            throw new InvalidDataException("识别出的音符过多，请截短音频或改用“干净”模式。");
        }
    }

    internal static IReadOnlyList<AudioTranscriptionNote> ExtractMelody(
        IReadOnlyList<AudioTranscriptionNote> detected)
    {
        const double chordWindowSeconds = 0.045;
        var sorted = detected
            .OrderBy(n => n.StartSeconds)
            .ThenByDescending(n => n.Amplitude)
            .ThenByDescending(n => n.Pitch)
            .ToArray();
        var selected = new List<AudioTranscriptionNote>();
        int? previousPitch = null;

        for (var index = 0; index < sorted.Length;)
        {
            var groupStart = sorted[index].StartSeconds;
            var group = new List<AudioTranscriptionNote>();
            while (index < sorted.Length
                   && sorted[index].StartSeconds - groupStart <= chordWindowSeconds)
            {
                group.Add(sorted[index++]);
            }

            var choice = group
                .OrderByDescending(note => MelodyCandidateScore(note, previousPitch))
                .ThenByDescending(note => note.Pitch)
                .First();
            selected.Add(choice);
            previousPitch = choice.Pitch;
        }

        // The recommended track must be monophonic. Shorten a sustained note
        // when the next selected note begins before it ends.
        for (var index = 0; index + 1 < selected.Count; index++)
        {
            var current = selected[index];
            var next = selected[index + 1];
            var latestDuration = next.StartSeconds - current.StartSeconds - 0.006;
            if (latestDuration > 0.015 && latestDuration < current.DurationSeconds)
            {
                selected[index] = current with { DurationSeconds = latestDuration };
            }
        }

        return selected;
    }

    private static double MelodyCandidateScore(AudioTranscriptionNote note, int? previousPitch)
    {
        var score = note.Amplitude * 4.0 + note.Pitch * 0.012;
        if (previousPitch is not null)
        {
            score -= Math.Abs(note.Pitch - previousPitch.Value) * 0.07;
        }
        return score;
    }

    private static List<AudioTranscriptionNote> Normalize(
        IReadOnlyList<AudioTranscriptionNote> source)
    {
        return source
            .Where(note =>
                double.IsFinite(note.StartSeconds)
                && double.IsFinite(note.DurationSeconds)
                && double.IsFinite(note.Amplitude)
                && note.StartSeconds is >= 0 and <= MaximumStartSeconds
                && note.DurationSeconds > 0
                && note.Pitch is >= 0 and <= 127)
            .Select(note => note with
            {
                DurationSeconds = Math.Clamp(note.DurationSeconds, 0.012, 120),
                Amplitude = Math.Clamp(note.Amplitude, 0.01, 1.0),
            })
            .OrderBy(note => note.StartSeconds)
            .ThenBy(note => note.Pitch)
            .ToList();
    }

    private static void WriteFile(
        string destination,
        IReadOnlyList<AudioTranscriptionNote> detected,
        IReadOnlyList<AudioTranscriptionNote> melody)
    {
        var file = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
        };

        file.Chunks.Add(BuildTrack("AI Melody", melody, includeTempo: true));
        file.Chunks.Add(BuildTrack("AI All Notes", detected, includeTempo: false));

        var fullPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath))
        {
            throw new IOException($"目标文件已存在：{Path.GetFileName(fullPath)}");
        }

        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
            {
                file.Write(stream, MidiFileFormat.MultiTrack);
            }
            File.Move(temporaryPath, fullPath, false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static TrackChunk BuildTrack(
        string name,
        IReadOnlyList<AudioTranscriptionNote> notes,
        bool includeTempo)
    {
        var track = new TrackChunk();
        track.Events.Add(new SequenceTrackNameEvent(name));
        if (includeTempo) track.Events.Add(new SetTempoEvent(500_000));
        track.Events.Add(new ProgramChangeEvent(new SevenBitNumber(22)));

        var events = new List<(long Tick, int Order, MidiEvent Event)>(notes.Count * 2);
        foreach (var note in notes)
        {
            var startTick = Math.Max(0, ToTick(note.StartSeconds));
            var endTick = Math.Max(startTick + 1, ToTick(note.StartSeconds + note.DurationSeconds));
            var number = new SevenBitNumber((byte)note.Pitch);
            var velocity = new SevenBitNumber((byte)Math.Clamp(
                (int)Math.Round(note.Amplitude * 127, MidpointRounding.AwayFromZero),
                1,
                127));
            events.Add((startTick, 1, new NoteOnEvent(number, velocity)));
            events.Add((endTick, 0, new NoteOffEvent(number, new SevenBitNumber(0))));
        }

        long previousTick = 0;
        foreach (var item in events.OrderBy(item => item.Tick).ThenBy(item => item.Order))
        {
            item.Event.DeltaTime = item.Tick - previousTick;
            previousTick = item.Tick;
            track.Events.Add(item.Event);
        }
        return track;
    }

    private static long ToTick(double seconds) =>
        (long)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero);
}
