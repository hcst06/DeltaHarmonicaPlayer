namespace DeltaHarmonicaPlayer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            return SelfTest.Run();
        }

        if (args.Length == 3 && args[0].Equals("--migrate-ahk", StringComparison.OrdinalIgnoreCase))
        {
            AhkMigration.ConvertToMidi(args[1], args[2]);
            return 0;
        }

        if (args.Length == 6 && args[0].Equals("--jianpu-to-midi", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(args[4], out var bpm))
            {
                throw new ArgumentException("BPM 必须是整数。");
            }
            var result = JianpuConverter.WriteMidi(
                args[5],
                args[1],
                new JianpuOptions(args[3], bpm),
                args[2]);
            Console.WriteLine($"JIANPU_OK notes={result.Notes.Count} beats={result.TotalBeats:0.###}");
            return 0;
        }

        if (args.Length == 2 && args[0].Equals("--inspect-midi", StringComparison.OrdinalIgnoreCase))
        {
            var song = MidiLibrary.Load(args[1]);
            Console.WriteLine($"{song.Title}: {song.Tracks.Count} playable track(s)");
            foreach (var track in song.Tracks)
            {
                var plan = HarmonicaPlanner.Build(track.Notes, 1.0);
                Console.WriteLine($"  #{track.Index + 1} source-track={track.SourceTrack + 1} channel={track.Channel + 1} {track.Name}: source={track.Notes.Count}, play={plan.Notes.Count}, transpose={plan.Transpose}, dropped={plan.DroppedNotes}, left={plan.LeftMouseNotes}");
            }
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length == 1 && args[0].Equals("--smoke-ui", StringComparison.OrdinalIgnoreCase))
        {
            using var smokeForm = new MainForm();
            smokeForm.Shown += (_, _) => smokeForm.BeginInvoke(new Action(smokeForm.Close));
            Application.Run(smokeForm);
            return 0;
        }

        if (args.Length == 1 && args[0].Equals("--smoke-jianpu-ui", StringComparison.OrdinalIgnoreCase))
        {
            using var smokeForm = new JianpuForm();
            smokeForm.Shown += (_, _) => smokeForm.BeginInvoke(new Action(smokeForm.Close));
            Application.Run(smokeForm);
            return 0;
        }

        if (args.Length == 1 && args[0].Equals("--smoke-audio-ui", StringComparison.OrdinalIgnoreCase))
        {
            using var smokeForm = new AudioToMidiForm(closeWhenReadyForSmokeTest: true);
            Application.Run(smokeForm);
            return smokeForm.SmokeExitCode;
        }

        if (args.Length == 2 && args[0].Equals("--audio-e2e", StringComparison.OrdinalIgnoreCase))
        {
            using var e2eForm = new AudioToMidiForm(automatedAudioPath: args[1]);
            Application.Run(e2eForm);
            var generatedPath = e2eForm.CreatedPath;
            if (generatedPath is not null && File.Exists(generatedPath)) File.Delete(generatedPath);
            return e2eForm.SmokeExitCode;
        }

        Application.Run(new MainForm());
        return 0;
    }
}
