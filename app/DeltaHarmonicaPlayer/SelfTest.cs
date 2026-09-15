namespace DeltaHarmonicaPlayer;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            using (TimerResolutionScope.TryBegin())
            {
                // Verifies the native Windows timer entry points used when a
                // song starts, not just the managed planning code.
            }

            Assert(HarmonicaPlanner.FingeringsFor(60).Any(f => f.ScanCode == 0x02C && f.Modifiers == MouseModifier.None), "中音 1 应映射到 Z");
            Assert(HarmonicaPlanner.FingeringsFor(61).Any(f => f.ScanCode == 0x02C && f.Modifiers == MouseModifier.Semitone), "升半音应使用鼠标中键");
            Assert(HarmonicaPlanner.FingeringsFor(48).Any(f => f.ScanCode == 0x02C && f.Modifiers == MouseModifier.OctaveDown), "低八度应使用鼠标左键");
            Assert(HarmonicaPlanner.FingeringsFor(72).Any(f => f.ScanCode == 0x02C && f.Modifiers == MouseModifier.OctaveUp), "高八度应可使用鼠标右键");

            var plan = HarmonicaPlanner.Build(
                [new SongNote(1_000_000, 1_300_000, 60, 90), new SongNote(1_500_000, 1_800_000, 64, 90)],
                1.0);
            Assert(plan.Notes.Count == 2, "应保留两个旋律音符");
            Assert(plan.Notes[0].StartUs == 25_000, "应移除 MIDI 开头空白并预留修饰键建立时间");
            Assert(plan.LeftMouseNotes == 0, "常规音域不应误用鼠标左键");

            var lowPlan = HarmonicaPlanner.Build(
                [new SongNote(0, 300_000, 48, 90), new SongNote(400_000, 700_000, 52, 90)],
                1.0);
            Assert(lowPlan.Transpose == 0 && lowPlan.LeftMouseNotes == 2,
                "可演奏的低音应保持原调，并使用鼠标左键降八度");

            var modifierPlan = HarmonicaPlanner.Build(
                [new SongNote(0, 400_000, 60, 90), new SongNote(500_000, 800_000, 61, 90)],
                1.0);
            Assert(modifierPlan.Notes[0].EndUs <= modifierPlan.Notes[1].StartUs - 10_000,
                "修饰鼠标键应在下一个音符前获得建立时间");

            var screenshotNotation = JianpuConverter.Parse("1'1'1'1'6 56542 | 422422 55655", "C", 120);
            Assert(screenshotNotation.Success && screenshotNotation.Notes.Count == 21,
                "截图中的连续简谱应解析为 21 个音符");
            Assert(screenshotNotation.Notes.Take(4).All(n => n.MidiNote == 72),
                "数字后的撇号应表示高八度");

            var dottedNotation = JianpuConverter.Parse("1\u0307 2\u0323 #4 0 5- 6/", "C", 100);
            Assert(dottedNotation.Success && dottedNotation.Notes.Count == 5,
                "应支持上下点、升音、休止、延音和减时");
            Assert(dottedNotation.Notes[0].MidiNote == 72 && dottedNotation.Notes[1].MidiNote == 50,
                "Unicode 上下点的八度解析错误");
            Assert(Math.Abs(dottedNotation.TotalBeats - 6.5) < 0.001,
                "简谱时值计算错误");

            var headerNotation = JianpuConverter.Parse("1=C BPM=120 4/4 | 123", new JianpuOptions());
            Assert(headerNotation.Success && headerNotation.Notes.Count == 3,
                "常见拍号 4/4 不应被误写成两个音符");
            Assert(headerNotation.Notes.Select(n => n.MidiNote).SequenceEqual([60, 62, 64]),
                "拍号后的旋律解析错误");

            var durationShorthand = JianpuConverter.Parse("1/2", "C", 120);
            Assert(durationShorthand.Success && durationShorthand.Notes.Count == 2
                && Math.Abs(durationShorthand.TotalBeats - 1.5) < 0.001,
                "音符减时写法 1/2 不应被误判为拍号");

            var roundTripPath = Path.Combine(Path.GetTempPath(), $"DeltaHarmonicaPlayer-{Guid.NewGuid():N}.mid");
            try
            {
                JianpuConverter.WriteMidi(headerNotation, roundTripPath, "Jianpu Self Test");
                var roundTrip = MidiLibrary.Load(roundTripPath);
                Assert(roundTrip.Tracks.Count == 1 && roundTrip.Tracks[0].Notes.Count == 3,
                    "简谱 MIDI 写入后重新读取失败");
                Assert(roundTrip.Tracks[0].Notes.Select(n => n.Pitch).SequenceEqual([60, 62, 64]),
                    "简谱 MIDI 往返后的音高发生变化");
            }
            finally
            {
                if (File.Exists(roundTripPath)) File.Delete(roundTripPath);
            }

            var audioMidiPath = Path.Combine(Path.GetTempPath(), $"DeltaHarmonicaAudio-{Guid.NewGuid():N}.mid");
            try
            {
                var audioRequest = new AudioTranscriptionRequest(
                    "complete",
                    "Audio Self Test",
                    "test.wav",
                    "standard",
                    true,
                    [
                        new AudioTranscriptionNote(0.0, 0.40, 60, 0.90),
                        new AudioTranscriptionNote(0.01, 0.35, 67, 0.40),
                        new AudioTranscriptionNote(0.50, 0.50, 62, 0.85),
                        new AudioTranscriptionNote(0.51, 0.30, 74, 0.30),
                        new AudioTranscriptionNote(1.00, 0.45, 64, 0.80),
                    ]);
                var written = AudioMidiWriter.Write(audioRequest, audioMidiPath);
                Assert(written.DetectedNotes == 5 && written.MelodyNotes == 3,
                    "AI 扒谱结果应生成原始和简化旋律两种轨道");
                var audioMidi = MidiLibrary.Load(audioMidiPath);
                Assert(audioMidi.Tracks.Count == 2,
                    "AI 扒谱 MIDI 应包含推荐旋律和全部识别音符两条轨道");
                Assert(audioMidi.Tracks.Any(t => t.Name == "AI Melody" && t.Notes.Count == 3),
                    "AI 推荐旋律轨道写入失败");
                Assert(audioMidi.Tracks.Any(t => t.Name == "AI All Notes" && t.Notes.Count == 5),
                    "AI 全部音符轨道写入失败");
                Assert(MidiLibrary.PickDefaultTrack(audioMidi).Name == "AI Melody",
                    "播放器应默认选择 AI 推荐旋律轨道");
            }
            finally
            {
                if (File.Exists(audioMidiPath)) File.Delete(audioMidiPath);
            }

            using (var stopTest = new CancellationTokenSource())
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                stopTest.CancelAfter(30);
                try
                {
                    PlaybackEngine.WaitUntil(stopwatch, 5_000_000, stopTest.Token);
                    throw new InvalidOperationException("等待应被取消令牌打断");
                }
                catch (OperationCanceledException)
                {
                    Assert(stopwatch.ElapsedMilliseconds < 500, "F10 应在长音/长休止期间快速停止");
                }
            }

            const string ahk = """
                F5::{
                    if (!WaitUntil(t0, 100)) {
                    }
                    Send("{Blind}{sc02C down}")
                    if (!WaitUntil(t0, 300)) {
                    }
                    Send("{Blind}{sc02C up}")
                    Send("{Blind}{RButton down}")
                    if (!WaitUntil(t0, 400)) {
                    }
                    Send("{Blind}{sc02D down}")
                    if (!WaitUntil(t0, 550)) {
                    }
                    Send("{Blind}{sc02D up}")
                }
                """;
            var migrated = AhkMigration.ParseNotes(ahk);
            Assert(migrated.Count == 2, "AHK 迁移应还原两个音符");
            Assert(migrated[0].Pitch == 60 && migrated[1].Pitch == 74, "AHK 修饰键音高还原错误");

            Console.WriteLine("SELF_TEST_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELF_TEST_FAILED: {ex.Message}");
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
