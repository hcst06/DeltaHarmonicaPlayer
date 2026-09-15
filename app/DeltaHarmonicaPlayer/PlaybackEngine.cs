using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeltaHarmonicaPlayer;

internal sealed record PlaybackStatus(string Text, int Current, int Total, bool Running);

internal sealed class PlaybackEngine : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _completion;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                // A cancelled session remains active until its worker reaches
                // finally. This prevents a fast F10 -> F9 restart from letting
                // the old cleanup release inputs owned by the new session.
                return _cancellation is not null;
            }
        }
    }

    public async Task PlayAsync(PlayPlan plan, int countdownSeconds, IProgress<PlaybackStatus> progress)
    {
        CancellationTokenSource cancellation;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_cancellation is not null)
            {
                return;
            }
            _cancellation = cancellation = new CancellationTokenSource();
            _completion = completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            for (var second = Math.Max(0, countdownSeconds); second > 0; second--)
            {
                progress.Report(new PlaybackStatus($"{second} 秒后开始，请切回游戏…", 0, plan.Notes.Count, true));
                await Task.Delay(1000, cancellation.Token);
            }

            progress.Report(new PlaybackStatus("正在演奏；按 F10 可随时停止。", 0, plan.Notes.Count, true));
            await Task.Run(() => PlayCore(plan, progress, cancellation.Token), cancellation.Token);
            progress.Report(new PlaybackStatus("演奏完成。", plan.Notes.Count, plan.Notes.Count, false));
        }
        catch (OperationCanceledException)
        {
            progress.Report(new PlaybackStatus("已停止，并已松开全部按键。", 0, plan.Notes.Count, false));
        }
        finally
        {
            var released = InputSender.ReleaseHeld();
            if (!released)
            {
                progress.Report(new PlaybackStatus("部分按键未能松开，请再按一次 F10 重试。", 0, plan.Notes.Count, false));
            }
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation.Dispose();
                    _cancellation = null;
                    _completion = null;
                }
            }
            completion.TrySetResult();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
        }
    }

    public Task WaitForStopAsync()
    {
        lock (_gate)
        {
            return _completion?.Task ?? Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        Stop();
        InputSender.ReleaseHeld();
    }

    private static void PlayCore(PlayPlan plan, IProgress<PlaybackStatus> progress, CancellationToken cancellation)
    {
        using var timerResolution = TimerResolutionScope.TryBegin();
        var stopwatch = Stopwatch.StartNew();
        var currentModifiers = MouseModifier.None;

        for (var i = 0; i < plan.Notes.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var note = plan.Notes[i];

            var modifierLeadUs = HarmonicaPlanner.ModifierLeadUs(note.Fingering.Modifiers);
            WaitUntil(stopwatch, Math.Max(0, note.StartUs - modifierLeadUs), cancellation);
            cancellation.ThrowIfCancellationRequested();
            ApplyModifiers(ref currentModifiers, note.Fingering.Modifiers);
            WaitUntil(stopwatch, note.StartUs, cancellation);

            cancellation.ThrowIfCancellationRequested();
            InputSender.Key(note.Fingering.ScanCode, true);
            try
            {
                WaitUntil(stopwatch, note.EndUs, cancellation);
            }
            finally
            {
                try
                {
                    InputSender.Key(note.Fingering.ScanCode, false);
                }
                finally
                {
                    // Mouse modifiers are held only while their note sounds.
                    // This matches the original profile and minimizes accidental
                    // left-button (fire) time in the game.
                    ApplyModifiers(ref currentModifiers, MouseModifier.None);
                }
            }

            if (i == 0 || (i + 1) % 10 == 0 || i + 1 == plan.Notes.Count)
            {
                progress.Report(new PlaybackStatus(
                    $"正在演奏：{i + 1}/{plan.Notes.Count}；按 F10 停止。",
                    i + 1,
                    plan.Notes.Count,
                    true));
            }
        }
    }

    internal static void WaitUntil(Stopwatch stopwatch, long targetUs, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var elapsedUs = stopwatch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            var remainingUs = targetUs - elapsedUs;
            if (remainingUs <= 0)
            {
                return;
            }

            if (remainingUs > 4_000)
            {
                var waitMs = Math.Max(1, (int)Math.Min(int.MaxValue, remainingUs / 1000 - 2));
                if (cancellation.WaitHandle.WaitOne(waitMs))
                {
                    cancellation.ThrowIfCancellationRequested();
                }
            }
            else
            {
                Thread.SpinWait(80);
            }
        }
    }

    private static void ApplyModifiers(ref MouseModifier current, MouseModifier target)
    {
        var release = current & ~target;
        var press = target & ~current;

        if ((release & MouseModifier.OctaveDown) != 0) InputSender.Mouse(MouseButton.Left, false);
        if ((release & MouseModifier.OctaveUp) != 0) InputSender.Mouse(MouseButton.Right, false);
        if ((release & MouseModifier.Semitone) != 0) InputSender.Mouse(MouseButton.Middle, false);

        if ((press & MouseModifier.OctaveDown) != 0) InputSender.Mouse(MouseButton.Left, true);
        if ((press & MouseModifier.OctaveUp) != 0) InputSender.Mouse(MouseButton.Right, true);
        if ((press & MouseModifier.Semitone) != 0) InputSender.Mouse(MouseButton.Middle, true);

        current = target;
    }
}

internal sealed class TimerResolutionScope : IDisposable
{
    private readonly bool _active;

    private TimerResolutionScope(bool active) => _active = active;

    public static TimerResolutionScope TryBegin() => new(TimeBeginPeriod(1) == 0);

    public void Dispose()
    {
        if (_active) _ = TimeEndPeriod(1);
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint periodMilliseconds);
}

internal enum MouseButton
{
    Left,
    Middle,
    Right,
}

internal static class InputSender
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFScanCode = 0x0008;
    private static readonly object Gate = new();
    private static readonly HashSet<int> HeldScanCodes = [];
    private static MouseModifier _heldMouse;

    public static bool HasHeldInputs
    {
        get
        {
            lock (Gate)
            {
                return HeldScanCodes.Count > 0 || _heldMouse != MouseModifier.None;
            }
        }
    }

    public static void Key(int scanCode, bool down)
    {
        lock (Gate)
        {
            if (down ? HeldScanCodes.Contains(scanCode) : !HeldScanCodes.Contains(scanCode))
            {
                return;
            }

            var input = new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        Scan = (ushort)scanCode,
                        Flags = KeyEventFScanCode | (down ? 0u : KeyEventFKeyUp),
                    },
                },
            };
            Send(input);
            if (down) HeldScanCodes.Add(scanCode);
            else HeldScanCodes.Remove(scanCode);
        }
    }

    public static void Mouse(MouseButton button, bool down)
    {
        var flags = button switch
        {
            MouseButton.Left => down ? 0x0002u : 0x0004u,
            MouseButton.Right => down ? 0x0008u : 0x0010u,
            MouseButton.Middle => down ? 0x0020u : 0x0040u,
            _ => 0u,
        };

        var modifier = button switch
        {
            MouseButton.Left => MouseModifier.OctaveDown,
            MouseButton.Middle => MouseModifier.Semitone,
            MouseButton.Right => MouseModifier.OctaveUp,
            _ => MouseModifier.None,
        };

        lock (Gate)
        {
            var isHeld = (_heldMouse & modifier) != 0;
            if (down == isHeld)
            {
                return;
            }

            var input = new Input
            {
                Type = InputMouse,
                Union = new InputUnion { Mouse = new MouseInput { Flags = flags } },
            };
            Send(input);
            if (down) _heldMouse |= modifier;
            else _heldMouse &= ~modifier;
        }
    }

    public static bool ReleaseHeld()
    {
        lock (Gate)
        {
            foreach (var scanCode in HeldScanCodes.ToArray())
            {
                TrySendRelease(CreateKeyInput(scanCode, false), () => HeldScanCodes.Remove(scanCode));
            }

            foreach (var (button, modifier) in new[]
                     {
                         (MouseButton.Left, MouseModifier.OctaveDown),
                         (MouseButton.Middle, MouseModifier.Semitone),
                         (MouseButton.Right, MouseModifier.OctaveUp),
                     })
            {
                if ((_heldMouse & modifier) == 0) continue;
                TrySendRelease(CreateMouseInput(button, false), () => _heldMouse &= ~modifier);
            }

            return HeldScanCodes.Count == 0 && _heldMouse == MouseModifier.None;
        }
    }

    private static void Send(Input input)
    {
        var inputs = new[] { input };
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能发送模拟输入。请确认游戏与播放器使用相同的管理员权限。 ");
        }
    }

    private static bool TrySendRelease(Input input, Action onSuccess)
    {
        try
        {
            Send(input);
            onSuccess();
            return true;
        }
        catch
        {
            // Best effort during cancellation/application shutdown. Keep the
            // held-state entry so a later cleanup can retry it.
            return false;
        }
    }

    private static Input CreateKeyInput(int scanCode, bool down) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                Scan = (ushort)scanCode,
                Flags = KeyEventFScanCode | (down ? 0u : KeyEventFKeyUp),
            },
        },
    };

    private static Input CreateMouseInput(MouseButton button, bool down)
    {
        var flags = button switch
        {
            MouseButton.Left => down ? 0x0002u : 0x0004u,
            MouseButton.Right => down ? 0x0008u : 0x0010u,
            MouseButton.Middle => down ? 0x0020u : 0x0040u,
            _ => 0u,
        };
        return new Input
        {
            Type = InputMouse,
            Union = new InputUnion { Mouse = new MouseInput { Flags = flags } },
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}
