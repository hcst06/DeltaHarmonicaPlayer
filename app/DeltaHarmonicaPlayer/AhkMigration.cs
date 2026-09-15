using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace DeltaHarmonicaPlayer;

internal static partial class AhkMigration
{
    private static readonly IReadOnlyDictionary<int, int> DegreeByScanCode = new Dictionary<int, int>
    {
        [0x02C] = 0,
        [0x02D] = 2,
        [0x02E] = 4,
        [0x02F] = 5,
        [0x030] = 7,
        [0x031] = 9,
        [0x032] = 11,
        [0x033] = 12,
    };

    public static void ConvertToMidi(string ahkPath, string midiPath)
    {
        if (!File.Exists(ahkPath))
        {
            throw new FileNotFoundException("找不到旧演奏脚本。", ahkPath);
        }

        var notes = ParseNotes(File.ReadAllText(ahkPath));
        if (notes.Count == 0)
        {
            throw new InvalidDataException("脚本中没有找到可还原的演奏音符。");
        }

        WriteMidi(notes, Path.GetFileNameWithoutExtension(ahkPath), midiPath);
    }

    internal static IReadOnlyList<SongNote> ParseNotes(string script)
    {
        var playIndex = script.IndexOf("F5::{", StringComparison.OrdinalIgnoreCase);
        if (playIndex < 0)
        {
            playIndex = script.IndexOf("F9::{", StringComparison.OrdinalIgnoreCase);
        }
        if (playIndex >= 0)
        {
            script = script[playIndex..];
        }

        long currentMs = 0;
        var modifiers = MouseModifier.None;
        var active = new Dictionary<int, (long StartMs, int Pitch, int Velocity)>();
        var notes = new List<SongNote>();

        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.Trim();
            var wait = WaitRegex().Match(line);
            if (wait.Success && long.TryParse(wait.Groups[1].Value, out var parsedMs))
            {
                currentMs = parsedMs;
                continue;
            }

            var send = SendRegex().Match(line);
            if (!send.Success)
            {
                continue;
            }

            var token = send.Groups[1].Value;
            var isDown = send.Groups[2].Value.Equals("down", StringComparison.OrdinalIgnoreCase);
            if (TryApplyMouse(token, isDown, ref modifiers))
            {
                continue;
            }

            if (!token.StartsWith("sc", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(token.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var scanCode)
                || !DegreeByScanCode.TryGetValue(scanCode, out var degree))
            {
                continue;
            }

            if (isDown)
            {
                var pitch = 60 + degree + ModifierDelta(modifiers);
                active[scanCode] = (currentMs, pitch, 90);
            }
            else if (active.Remove(scanCode, out var started))
            {
                var endMs = Math.Max(currentMs, started.StartMs + 12);
                notes.Add(new SongNote(started.StartMs * 1000, endMs * 1000, started.Pitch, started.Velocity));
            }
        }

        foreach (var started in active.Values)
        {
            notes.Add(new SongNote(started.StartMs * 1000, (started.StartMs + 80) * 1000, started.Pitch, started.Velocity));
        }

        return notes.OrderBy(n => n.StartUs).ThenByDescending(n => n.Pitch).ToArray();
    }

    private static bool TryApplyMouse(string token, bool down, ref MouseModifier modifiers)
    {
        var value = token.ToLowerInvariant() switch
        {
            "lbutton" => MouseModifier.OctaveDown,
            "mbutton" => MouseModifier.Semitone,
            "rbutton" => MouseModifier.OctaveUp,
            _ => MouseModifier.None,
        };
        if (value == MouseModifier.None)
        {
            return false;
        }

        modifiers = down ? modifiers | value : modifiers & ~value;
        return true;
    }

    private static int ModifierDelta(MouseModifier modifiers)
    {
        var delta = 0;
        if ((modifiers & MouseModifier.OctaveDown) != 0) delta -= 12;
        if ((modifiers & MouseModifier.Semitone) != 0) delta += 1;
        if ((modifiers & MouseModifier.OctaveUp) != 0) delta += 12;
        return delta;
    }

    private static void WriteMidi(IReadOnlyList<SongNote> notes, string title, string outputPath)
    {
        const short ticksPerQuarter = 480;
        const long microsecondsPerQuarter = 500_000;
        const double microsecondsPerTick = microsecondsPerQuarter / (double)ticksPerQuarter;

        var file = new MidiFile { TimeDivision = new TicksPerQuarterNoteTimeDivision(ticksPerQuarter) };
        var track = new TrackChunk();
        track.Events.Add(new SequenceTrackNameEvent(title));
        track.Events.Add(new SetTempoEvent(microsecondsPerQuarter));

        var events = new List<(long Tick, int Order, MidiEvent Event)>();
        foreach (var note in notes)
        {
            var onTick = Math.Max(0, (long)Math.Round(note.StartUs / microsecondsPerTick));
            var offTick = Math.Max(onTick + 1, (long)Math.Round(note.EndUs / microsecondsPerTick));
            events.Add((onTick, 1, new NoteOnEvent(new SevenBitNumber((byte)Math.Clamp(note.Pitch, 0, 127)), new SevenBitNumber((byte)Math.Clamp(note.Velocity, 1, 127)))));
            events.Add((offTick, 0, new NoteOffEvent(new SevenBitNumber((byte)Math.Clamp(note.Pitch, 0, 127)), new SevenBitNumber(0))));
        }

        long previousTick = 0;
        foreach (var item in events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
        {
            item.Event.DeltaTime = item.Tick - previousTick;
            previousTick = item.Tick;
            track.Events.Add(item.Event);
        }

        file.Chunks.Add(track);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var stream = File.Create(outputPath);
        file.Write(stream, MidiFileFormat.SingleTrack);
    }

    [GeneratedRegex(@"WaitUntil\(t0,\s*(\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex WaitRegex();

    [GeneratedRegex("""Send\("\{Blind\}\{([^}\s]+)\s+(down|up)\}"\)""", RegexOptions.IgnoreCase)]
    private static partial Regex SendRegex();
}
