namespace DeltaHarmonicaPlayer;

internal enum MouseModifier
{
    None = 0,
    OctaveDown = 1,
    Semitone = 2,
    OctaveUp = 4,
}

internal sealed record Fingering(int ScanCode, MouseModifier Modifiers, int Pitch);

internal sealed record PlannedNote(long StartUs, long EndUs, Fingering Fingering);

internal sealed record PlayPlan(
    IReadOnlyList<PlannedNote> Notes,
    int Transpose,
    int SourceNotes,
    int DroppedNotes,
    int LeftMouseNotes,
    TimeSpan Duration);

internal static class HarmonicaPlanner
{
    private static readonly int[] DegreeOffsets = [0, 2, 4, 5, 7, 9, 11, 12];
    private static readonly int[] ScanCodes = [0x02C, 0x02D, 0x02E, 0x02F, 0x030, 0x031, 0x032, 0x033];
    private const int BaseMidiNote = 60;

    public static PlayPlan Build(IReadOnlyList<SongNote> source, double speed)
    {
        const long playbackLeadUs = 25_000;
        if (source.Count == 0)
        {
            throw new InvalidDataException("所选音轨没有音符。");
        }

        speed = Math.Clamp(speed, 0.25, 2.0);
        var melody = CollapseChords(source);
        // Keep the MIDI's written pitches. Mouse-left is a legitimate low-octave
        // control, and silently moving an entire melody makes familiar songs
        // sound out of tune. Out-of-range notes are reported instead.
        const int transpose = 0;
        var result = new List<PlannedNote>(melody.Count);
        var previousModifiers = MouseModifier.None;
        var dropped = 0;
        var firstStart = melody[0].StartUs;

        for (var i = 0; i < melody.Count; i++)
        {
            var note = melody[i];
            var candidates = FingeringsFor(note.Pitch + transpose);
            if (candidates.Count == 0)
            {
                dropped++;
                continue;
            }

            var fingering = candidates
                .OrderBy(f => FingeringRisk(f.Modifiers))
                .ThenBy(f => ModifierChangeCost(previousModifiers, f.Modifiers))
                .ThenBy(f => Math.Abs(f.Pitch - BaseMidiNote))
                .First();
            previousModifiers = fingering.Modifiers;

            var startUs = playbackLeadUs + (long)((note.StartUs - firstStart) / speed);
            var sourceEndUs = playbackLeadUs + (long)((note.EndUs - firstStart) / speed);
            var endUs = Math.Max(sourceEndUs, startUs + 12_000);

            result.Add(new PlannedNote(startUs, endUs, fingering));
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("这条旋律超出了口风琴音域，无法生成演奏计划。");
        }

        // A modifier must be established before the next keyboard note. End the
        // previous note early where the MIDI spacing allows a full 20 ms lead.
        for (var i = 0; i + 1 < result.Count; i++)
        {
            var current = result[i];
            var next = result[i + 1];
            var modifierLeadUs = ModifierLeadUs(next.Fingering.Modifiers);
            var reserveUs = modifierLeadUs == 0 ? 5_000L : modifierLeadUs + 2_000L;
            var latestEndUs = next.StartUs - reserveUs;
            var minimumEndUs = current.StartUs + 8_000L;
            var adjustedEndUs = Math.Max(minimumEndUs, Math.Min(current.EndUs, latestEndUs));
            adjustedEndUs = Math.Min(adjustedEndUs, Math.Max(minimumEndUs, next.StartUs - 2_000L));
            result[i] = current with { EndUs = adjustedEndUs };
        }

        var leftMouseNotes = result.Count(n => (n.Fingering.Modifiers & MouseModifier.OctaveDown) != 0);
        var duration = TimeSpan.FromMilliseconds(result[^1].EndUs / 1000.0);
        return new PlayPlan(result, transpose, melody.Count, dropped, leftMouseNotes, duration);
    }

    internal static IReadOnlyList<Fingering> FingeringsFor(int pitch)
    {
        var candidates = new List<Fingering>();
        foreach (var (octaveDelta, octaveMask) in new[]
                 {
                     (-12, MouseModifier.OctaveDown),
                     (0, MouseModifier.None),
                     (12, MouseModifier.OctaveUp),
                 })
        {
            for (var sharp = 0; sharp <= 1; sharp++)
            {
                for (var i = 0; i < DegreeOffsets.Length; i++)
                {
                    var candidatePitch = BaseMidiNote + octaveDelta + sharp + DegreeOffsets[i];
                    if (candidatePitch != pitch)
                    {
                        continue;
                    }

                    var mask = octaveMask | (sharp == 1 ? MouseModifier.Semitone : MouseModifier.None);
                    candidates.Add(new Fingering(ScanCodes[i], mask, pitch));
                }
            }
        }
        return candidates;
    }

    internal static long ModifierLeadUs(MouseModifier modifiers)
    {
        if ((modifiers & (MouseModifier.OctaveDown | MouseModifier.OctaveUp)) != 0)
        {
            return 12_000L;
        }
        if ((modifiers & MouseModifier.Semitone) != 0)
        {
            return 8_000L;
        }
        return 0L;
    }

    private static List<SongNote> CollapseChords(IReadOnlyList<SongNote> source)
    {
        const long chordWindowUs = 12_000;
        var sorted = source.OrderBy(n => n.StartUs).ThenByDescending(n => n.Pitch).ToList();
        var result = new List<SongNote>();

        for (var i = 0; i < sorted.Count;)
        {
            var groupStart = sorted[i].StartUs;
            var group = new List<SongNote>();
            while (i < sorted.Count && sorted[i].StartUs - groupStart <= chordWindowUs)
            {
                group.Add(sorted[i++]);
            }

            result.Add(group
                .OrderByDescending(n => n.Pitch)
                .ThenByDescending(n => n.Velocity)
                .First());
        }
        return result;
    }

    private static int FingeringRisk(MouseModifier value)
    {
        var risk = 0;
        if ((value & MouseModifier.OctaveDown) != 0) risk += 120;
        if ((value & MouseModifier.OctaveUp) != 0) risk += 18;
        if ((value & MouseModifier.Semitone) != 0) risk += 6;
        return risk;
    }

    private static int ModifierChangeCost(MouseModifier from, MouseModifier to)
    {
        var changed = from ^ to;
        var cost = ModifierCount(changed) * 10;
        if ((from & (MouseModifier.OctaveDown | MouseModifier.OctaveUp)) != 0
            && (to & (MouseModifier.OctaveDown | MouseModifier.OctaveUp)) != 0
            && (from & (MouseModifier.OctaveDown | MouseModifier.OctaveUp)) != (to & (MouseModifier.OctaveDown | MouseModifier.OctaveUp)))
        {
            cost += 8;
        }
        return cost;
    }

    private static int ModifierCount(MouseModifier value)
    {
        var bits = (int)value;
        var count = 0;
        while (bits != 0)
        {
            count += bits & 1;
            bits >>= 1;
        }
        return count;
    }
}
