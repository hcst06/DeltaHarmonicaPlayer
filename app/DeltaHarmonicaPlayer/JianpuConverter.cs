using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace DeltaHarmonicaPlayer;

internal enum JianpuDiagnosticSeverity
{
    Warning,
    Error,
}

internal sealed record JianpuDiagnostic(
    JianpuDiagnosticSeverity Severity,
    int Position,
    int Line,
    int Column,
    string Message)
{
    public override string ToString()
    {
        var kind = Severity == JianpuDiagnosticSeverity.Error ? "错误" : "提示";
        return $"第 {Line} 行第 {Column} 列（位置 {Position}）{kind}：{Message}";
    }
}

internal sealed record JianpuNoteEvent(
    int SourcePosition,
    int Degree,
    int Accidental,
    int OctaveShift,
    int MidiNote,
    double StartBeat,
    double DurationBeats,
    int Velocity);

internal sealed record JianpuOptions(
    string Key = "C",
    int BeatsPerMinute = 120,
    int BaseOctave = 4,
    int Velocity = 90,
    short TicksPerQuarterNote = 480);

internal sealed record JianpuParseResult(
    string Key,
    int BeatsPerMinute,
    double TotalBeats,
    IReadOnlyList<JianpuNoteEvent> Notes,
    IReadOnlyList<JianpuDiagnostic> Diagnostics)
{
    public bool Success => Diagnostics.All(d => d.Severity != JianpuDiagnosticSeverity.Error);
}

/// <summary>
/// Converts a compact numbered-musical-notation (jianpu) melody to a type-0 MIDI file.
/// One unmodified number occupies one quarter-note beat.
/// </summary>
internal static class JianpuConverter
{
    private static readonly int[] MajorScaleOffsets = [0, 2, 4, 5, 7, 9, 11];

    private static readonly IReadOnlyDictionary<string, int> KeyOffsets =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["C"] = 0,
            ["B#"] = 12,
            ["C#"] = 1,
            ["DB"] = 1,
            ["D"] = 2,
            ["D#"] = 3,
            ["EB"] = 3,
            ["E"] = 4,
            ["FB"] = 4,
            ["E#"] = 5,
            ["F"] = 5,
            ["F#"] = 6,
            ["GB"] = 6,
            ["G"] = 7,
            ["G#"] = 8,
            ["AB"] = 8,
            ["A"] = 9,
            ["A#"] = 10,
            ["BB"] = 10,
            ["B"] = 11,
            ["CB"] = -1,
        };

    /// <summary>
    /// Parses jianpu. The key and BPM can be supplied through <paramref name="options"/>
    /// or overridden at the beginning of the text with, for example, "1=F# BPM=96".
    /// </summary>
    public static JianpuParseResult Parse(string notation, JianpuOptions? options = null)
    {
        notation ??= string.Empty;
        options ??= new JianpuOptions();

        var diagnostics = new List<JianpuDiagnostic>();
        var notes = new List<JianpuNoteEvent>();
        var key = NormalizeKey(options.Key);
        var bpm = options.BeatsPerMinute;
        var baseOctave = options.BaseOctave;
        var velocity = options.Velocity;

        if (!KeyOffsets.ContainsKey(key))
        {
            diagnostics.Add(At(notation, 0, $"不支持调号“{options.Key}”。可填写 C、C#、D、Eb、E、F、F#、G、Ab、A、Bb、B（也接受等音写法）。"));
            key = "C";
        }

        if (bpm is < 20 or > 400)
        {
            diagnostics.Add(At(notation, 0, "BPM 必须在 20 到 400 之间。"));
            bpm = 120;
        }

        if (baseOctave is < -1 or > 8)
        {
            diagnostics.Add(At(notation, 0, "基准八度必须在 -1 到 8 之间。"));
            baseOctave = 4;
        }

        if (velocity is < 1 or > 127)
        {
            diagnostics.Add(At(notation, 0, "力度必须在 1 到 127 之间。"));
            velocity = 90;
        }

        if (options.TicksPerQuarterNote <= 0)
        {
            diagnostics.Add(At(notation, 0, "每拍 ticks 必须大于 0。"));
        }

        var currentBeat = 0.0;
        LastElement? last = null;
        var hasMusic = false;
        var keyWasSetInText = false;
        var bpmWasSetInText = false;

        for (var index = 0; index < notation.Length;)
        {
            if (IsIgnored(notation[index]))
            {
                index++;
                continue;
            }

            if (TryReadKeyDirective(notation, index, out var rawKey, out var keyLength))
            {
                if (hasMusic)
                {
                    diagnostics.Add(At(notation, index, "调号设置要写在第一个音符之前。"));
                }
                else if (keyWasSetInText)
                {
                    diagnostics.Add(At(notation, index, "调号只能设置一次。"));
                }
                else
                {
                    var parsedKey = NormalizeKey(rawKey);
                    if (!KeyOffsets.ContainsKey(parsedKey))
                    {
                        diagnostics.Add(At(notation, index, $"无法识别调号“{rawKey}”。"));
                    }
                    else
                    {
                        key = parsedKey;
                        keyWasSetInText = true;
                    }
                }

                index += keyLength;
                continue;
            }

            if (StartsWithBpm(notation, index))
            {
                var start = index;
                if (!TryReadBpmDirective(notation, index, out var parsedBpm, out var bpmLength))
                {
                    diagnostics.Add(At(notation, start, "BPM 写法不正确，请使用 BPM=120。"));
                    index = SkipDirective(notation, index);
                    continue;
                }

                if (hasMusic)
                {
                    diagnostics.Add(At(notation, start, "BPM 设置要写在第一个音符之前。"));
                }
                else if (bpmWasSetInText)
                {
                    diagnostics.Add(At(notation, start, "BPM 只能设置一次。"));
                }
                else if (parsedBpm is < 20 or > 400)
                {
                    diagnostics.Add(At(notation, start, "BPM 必须在 20 到 400 之间。"));
                }
                else
                {
                    bpm = parsedBpm;
                    bpmWasSetInText = true;
                }

                index += bpmLength;
                continue;
            }

            if (!hasMusic && TryReadTimeSignature(notation, index, out var numerator, out var denominator, out var signatureLength))
            {
                if (numerator is < 1 or > 12 || denominator is not (2 or 4 or 8 or 16))
                {
                    diagnostics.Add(At(notation, index, $"无法识别拍号 {numerator}/{denominator}。支持 1–12 拍，分母可为 2、4、8 或 16。"));
                }
                index += signatureLength;
                continue;
            }

            var accidental = 0;
            var sourcePosition = index;
            if (IsSharp(notation[index]) || IsFlat(notation[index]))
            {
                accidental = IsSharp(notation[index]) ? 1 : -1;
                index++;
                while (index < notation.Length && IsIgnored(notation[index]))
                {
                    index++;
                }

                if (index >= notation.Length || notation[index] is < '0' or > '7')
                {
                    diagnostics.Add(At(notation, sourcePosition, "升降号后面必须跟 1 到 7 的音符数字。"));
                    continue;
                }
            }

            if (index < notation.Length && notation[index] is >= '0' and <= '7')
            {
                var digitPosition = index;
                var degree = notation[index] - '0';
                index++;
                hasMusic = true;

                if (degree == 0 && accidental != 0)
                {
                    diagnostics.Add(At(notation, sourcePosition, "休止符 0 不能加升降号。"));
                    accidental = 0;
                }

                var octaveShift = 0;
                while (index < notation.Length && IsOctaveMark(notation[index]))
                {
                    octaveShift += OctaveDelta(notation[index]);
                    index++;
                }

                var noteIndex = -1;
                if (degree != 0)
                {
                    var midiNote = ToMidiNote(key, baseOctave, degree, accidental, octaveShift);
                    if (midiNote is < 0 or > 127)
                    {
                        diagnostics.Add(At(notation, digitPosition, $"这个音超出了 MIDI 的 0 到 127 音域（计算结果为 {midiNote}）。"));
                    }
                    else
                    {
                        noteIndex = notes.Count;
                        notes.Add(new JianpuNoteEvent(
                            sourcePosition + 1,
                            degree,
                            accidental,
                            octaveShift,
                            midiNote,
                            currentBeat,
                            1.0,
                            velocity));
                    }
                }
                else if (octaveShift != 0)
                {
                    diagnostics.Add(At(notation, digitPosition, "休止符 0 不能加高低八度标记。"));
                }

                last = new LastElement(noteIndex, currentBeat, 1.0, 0, degree == 0);
                currentBeat += 1.0;
                continue;
            }

            if (IsOctaveMark(notation[index]))
            {
                if (last is null)
                {
                    diagnostics.Add(At(notation, index, "高低八度标记前面没有音符。"));
                }
                else if (last.IsRest)
                {
                    diagnostics.Add(At(notation, index, "休止符 0 不能加高低八度标记。"));
                }
                else if (last.NoteIndex >= 0)
                {
                    var oldNote = notes[last.NoteIndex];
                    var octaveShift = oldNote.OctaveShift + OctaveDelta(notation[index]);
                    var midiNote = ToMidiNote(key, baseOctave, oldNote.Degree, oldNote.Accidental, octaveShift);
                    if (midiNote is < 0 or > 127)
                    {
                        diagnostics.Add(At(notation, index, $"这个八度标记会使音符超出 MIDI 音域（计算结果为 {midiNote}）。"));
                    }
                    else
                    {
                        notes[last.NoteIndex] = oldNote with { OctaveShift = octaveShift, MidiNote = midiNote };
                    }
                }

                index++;
                continue;
            }

            if (notation[index] == '/')
            {
                if (last is null)
                {
                    diagnostics.Add(At(notation, index, "减时符 / 前面没有音符或休止符。"));
                }
                else
                {
                    var newBaseDuration = last.BaseDuration / 2.0;
                    if (newBaseDuration * options.TicksPerQuarterNote < 1.0)
                    {
                        diagnostics.Add(At(notation, index, $"减时符过多，时值已经短于 1 个 MIDI tick（每拍 {options.TicksPerQuarterNote} ticks）。"));
                    }
                    else
                    {
                        var delta = newBaseDuration - last.BaseDuration;
                        last = last with { BaseDuration = newBaseDuration };
                        currentBeat += delta;
                        if (last.NoteIndex >= 0)
                        {
                            var oldNote = notes[last.NoteIndex];
                            notes[last.NoteIndex] = oldNote with { DurationBeats = oldNote.DurationBeats + delta };
                        }
                    }
                }

                index++;
                continue;
            }

            if (IsExtension(notation[index]))
            {
                if (last is null)
                {
                    diagnostics.Add(At(notation, index, "延音符 - 前面没有音符或休止符。"));
                }
                else
                {
                    last = last with { ExtensionBeats = last.ExtensionBeats + 1 };
                    currentBeat += 1.0;
                    if (last.NoteIndex >= 0)
                    {
                        var oldNote = notes[last.NoteIndex];
                        notes[last.NoteIndex] = oldNote with { DurationBeats = oldNote.DurationBeats + 1.0 };
                    }
                }

                index++;
                continue;
            }

            diagnostics.Add(At(notation, index, $"无法识别字符“{notation[index]}”。"));
            index++;
        }

        if (!hasMusic)
        {
            diagnostics.Add(At(notation, 0, "没有找到简谱音符，请输入 1 到 7；0 表示休止。"));
        }
        else if (notes.Count == 0 && diagnostics.All(d => d.Severity != JianpuDiagnosticSeverity.Error))
        {
            diagnostics.Add(At(notation, 0, "简谱中只有休止符，没有可写入 MIDI 的音符。"));
        }

        return new JianpuParseResult(
            DisplayKey(key),
            bpm,
            currentBeat,
            notes.ToArray(),
            diagnostics.ToArray());
    }

    public static JianpuParseResult Parse(string notation, string key, int beatsPerMinute = 120) =>
        Parse(notation, new JianpuOptions(key, beatsPerMinute));

    /// <summary>
    /// Parses the notation and writes a single-track MIDI file. Returns the parse result
    /// so a caller can display the resolved key, BPM, events, and diagnostics.
    /// </summary>
    public static JianpuParseResult WriteMidi(
        string notation,
        string outputPath,
        JianpuOptions? options = null,
        string? title = null)
    {
        var result = Parse(notation, options);
        WriteMidi(result, outputPath, title, options?.TicksPerQuarterNote ?? 480);
        return result;
    }

    /// <summary>Writes an already parsed melody. Parse errors are reported in Chinese.</summary>
    public static void WriteMidi(
        JianpuParseResult result,
        string outputPath,
        string? title = null,
        short ticksPerQuarterNote = 480)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!result.Success)
        {
            throw new InvalidDataException(
                "简谱有错误，未生成 MIDI：" + Environment.NewLine
                + string.Join(Environment.NewLine, result.Diagnostics
                    .Where(d => d.Severity == JianpuDiagnosticSeverity.Error)
                    .Select(d => d.ToString())));
        }

        if (result.Notes.Count == 0)
        {
            throw new InvalidDataException("简谱中没有可写入 MIDI 的音符。");
        }

        if (ticksPerQuarterNote <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote), "每拍 ticks 必须大于 0。");
        }

        var microsecondsPerQuarter = 60_000_000L / result.BeatsPerMinute;
        var file = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(ticksPerQuarterNote),
        };
        var track = new TrackChunk();
        // Standard MIDI text has no encoding marker. Keep the internal track
        // name ASCII for compatibility; the Chinese song title remains in the
        // file name and in the player's library.
        var requestedTitle = title?.Trim();
        var trackName = !string.IsNullOrWhiteSpace(requestedTitle) && requestedTitle.All(c => c <= 0x7F)
            ? requestedTitle
            : "Jianpu Melody";
        track.Events.Add(new SequenceTrackNameEvent(trackName));
        track.Events.Add(new SetTempoEvent(microsecondsPerQuarter));
        track.Events.Add(new ProgramChangeEvent(new SevenBitNumber(22)));

        var midiEvents = new List<(long Tick, int Order, MidiEvent Event)>();
        foreach (var note in result.Notes)
        {
            var onTick = Math.Max(0, ToTick(note.StartBeat, ticksPerQuarterNote));
            var offTick = Math.Max(onTick + 1, ToTick(note.StartBeat + note.DurationBeats, ticksPerQuarterNote));
            var noteNumber = new SevenBitNumber((byte)note.MidiNote);
            midiEvents.Add((onTick, 1, new NoteOnEvent(noteNumber, new SevenBitNumber((byte)note.Velocity))));
            midiEvents.Add((offTick, 0, new NoteOffEvent(noteNumber, new SevenBitNumber(0))));
        }

        long previousTick = 0;
        foreach (var item in midiEvents.OrderBy(e => e.Tick).ThenBy(e => e.Order))
        {
            item.Event.DeltaTime = item.Tick - previousTick;
            previousTick = item.Tick;
            track.Events.Add(item.Event);
        }

        file.Chunks.Add(track);
        var fullPath = Path.GetFullPath(outputPath);
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
                file.Write(stream, MidiFileFormat.SingleTrack);
            }
            File.Move(temporaryPath, fullPath, false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static long ToTick(double beat, short ticksPerQuarterNote) =>
        (long)Math.Round(beat * ticksPerQuarterNote, MidpointRounding.AwayFromZero);

    private static int ToMidiNote(string key, int baseOctave, int degree, int accidental, int octaveShift)
    {
        var tonic = (baseOctave + 1) * 12 + KeyOffsets[key];
        return tonic + MajorScaleOffsets[degree - 1] + accidental + octaveShift * 12;
    }

    private static bool TryReadKeyDirective(string text, int start, out string key, out int length)
    {
        key = string.Empty;
        length = 0;
        if (start >= text.Length || text[start] != '1')
        {
            return false;
        }

        var index = start + 1;
        SkipWhitespace(text, ref index);
        if (index >= text.Length || (text[index] != '=' && text[index] != '＝'))
        {
            return false;
        }

        index++;
        SkipWhitespace(text, ref index);
        if (index >= text.Length || char.ToUpperInvariant(text[index]) is < 'A' or > 'G')
        {
            length = Math.Max(1, index - start);
            return true;
        }

        key = char.ToUpperInvariant(text[index]).ToString();
        index++;
        if (index < text.Length && (IsSharp(text[index]) || IsFlat(text[index])))
        {
            key += IsSharp(text[index]) ? "#" : "b";
            index++;
        }

        length = index - start;
        return true;
    }

    private static bool StartsWithBpm(string text, int start) =>
        start + 3 <= text.Length
        && text.AsSpan(start, 3).Equals("BPM", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadTimeSignature(
        string text,
        int start,
        out int numerator,
        out int denominator,
        out int length)
    {
        numerator = 0;
        denominator = 0;
        length = 0;
        if (start >= text.Length || !char.IsAsciiDigit(text[start]))
        {
            return false;
        }

        var slash = start;
        while (slash < text.Length && char.IsAsciiDigit(text[slash])) slash++;
        if (slash == start || slash >= text.Length || text[slash] != '/')
        {
            return false;
        }

        var denominatorStart = slash + 1;
        var end = denominatorStart;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        if (end == denominatorStart)
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(start, slash - start), out numerator)
            || !int.TryParse(text.AsSpan(denominatorStart, end - denominatorStart), out denominator))
        {
            return false;
        }

        // Preserve the common duration shorthand 1/2, but treat every other
        // leading fraction as a time signature (valid or diagnostic-worthy).
        if (numerator == 1)
        {
            return false;
        }

        length = end - start;
        return true;
    }

    private static bool TryReadBpmDirective(string text, int start, out int bpm, out int length)
    {
        bpm = 0;
        length = 0;
        if (!StartsWithBpm(text, start))
        {
            return false;
        }

        var index = start + 3;
        SkipWhitespace(text, ref index);
        if (index >= text.Length || (text[index] != '=' && text[index] != '＝'))
        {
            return false;
        }

        index++;
        SkipWhitespace(text, ref index);
        var digitsStart = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        if (digitsStart == index || !int.TryParse(text.AsSpan(digitsStart, index - digitsStart), out bpm))
        {
            return false;
        }

        length = index - start;
        return true;
    }

    private static int SkipDirective(string text, int start)
    {
        var index = start;
        while (index < text.Length && text[index] is not '\r' and not '\n' and not '|')
        {
            index++;
        }
        return Math.Max(start + 1, index);
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    private static JianpuDiagnostic At(string text, int zeroBasedPosition, string message)
    {
        zeroBasedPosition = Math.Clamp(zeroBasedPosition, 0, text.Length);
        var line = 1;
        var column = 1;
        for (var i = 0; i < zeroBasedPosition; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return new JianpuDiagnostic(
            JianpuDiagnosticSeverity.Error,
            zeroBasedPosition + 1,
            line,
            column,
            message);
    }

    private static string NormalizeKey(string? key)
    {
        var value = (key ?? string.Empty).Trim()
            .Replace("♯", "#", StringComparison.Ordinal)
            .Replace("♭", "b", StringComparison.Ordinal);
        if (value.StartsWith("1=", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("1＝", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..].Trim();
        }

        if (value.Length == 0)
        {
            return value;
        }

        value = char.ToUpperInvariant(value[0]) + value[1..];
        if (value.Length > 1 && value[1] == 'B')
        {
            value = value[0] + "b";
        }
        return value.ToUpperInvariant();
    }

    private static string DisplayKey(string normalizedKey) => normalizedKey switch
    {
        "DB" => "Db",
        "EB" => "Eb",
        "GB" => "Gb",
        "AB" => "Ab",
        "BB" => "Bb",
        "CB" => "Cb",
        "FB" => "Fb",
        _ => normalizedKey,
    };

    private static bool IsIgnored(char value) => char.IsWhiteSpace(value) || value is '|' or '｜';

    private static bool IsSharp(char value) => value is '#' or '♯';

    private static bool IsFlat(char value) => value is 'b' or '♭';

    private static bool IsExtension(char value) => value is '-' or '—' or '–';

    private static bool IsOctaveMark(char value) => value is '\'' or ',' or '，' or '\u0307' or '\u0323';

    private static int OctaveDelta(char value) => value is '\'' or '\u0307' ? 1 : -1;

    private sealed record LastElement(
        int NoteIndex,
        double StartBeat,
        double BaseDuration,
        int ExtensionBeats,
        bool IsRest);
}
