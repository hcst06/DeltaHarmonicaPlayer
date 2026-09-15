using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeltaHarmonicaPlayer;

internal sealed record SongListItem(string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public override string ToString() => Name;
}

internal sealed class MainForm : Form
{
    private const int HotkeyStartId = 0xD901;
    private const int HotkeyStopId = 0xD902;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly ListBox _songs = new();
    private readonly ComboBox _tracks = new();
    private readonly NumericUpDown _speed = new();
    private readonly Label _songInfo = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _playButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _importButton = new();
    private readonly Button _audioButton = new();
    private readonly Button _jianpuButton = new();
    private readonly Button _refreshButton = new();
    private readonly Label _hotkeyInfo = new();
    private readonly PlaybackEngine _player = new();
    private PlayPlan? _plan;
    private bool _suppressTrackChange;
    private bool _startHotkeyRegistered;
    private bool _stopHotkeyRegistered;
    private bool _closeAfterPlayback;

    public MainForm()
    {
        Text = "三角洲口风琴播放器";
        Width = 920;
        Height = 610;
        MinimumSize = new Size(780, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 247, 250);
        KeyPreview = true;

        BuildInterface();
        Shown += (_, _) =>
        {
            RefreshLibrary();
            RegisterGlobalHotkeys();
        };
        FormClosing += HandleFormClosing;
        FormClosed += (_, _) => _player.Dispose();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey)
        {
            var id = message.WParam.ToInt32();
            if (id == HotkeyStartId)
            {
                _ = StartPlaybackAsync(0);
                return;
            }
            if (id == HotkeyStopId)
            {
                StopPlayback();
                return;
            }
        }
        base.WndProc(ref message);
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 2,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var warning = new Label
        {
            Text = "⚠ 自动按键和鼠标输入可能违反游戏规则并导致封禁。请自行承担风险，切勿在对局中使用。",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 0, 0, 14),
            BackColor = Color.FromArgb(255, 243, 205),
            ForeColor = Color.FromArgb(102, 77, 3),
        };
        root.SetColumnSpan(warning, 2);
        root.Controls.Add(warning, 0, 0);

        var libraryPanel = CreateCard();
        libraryPanel.Margin = new Padding(0, 0, 12, 0);
        root.Controls.Add(libraryPanel, 0, 1);

        var libraryTitle = new Label
        {
            Text = "我的 MIDI 曲库",
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font(Font, FontStyle.Bold),
        };
        libraryPanel.Controls.Add(libraryTitle);

        _songs.Dock = DockStyle.Fill;
        _songs.BorderStyle = BorderStyle.FixedSingle;
        _songs.IntegralHeight = false;
        _songs.SelectedIndexChanged += (_, _) => LoadSelectedSong();
        libraryPanel.Controls.Add(_songs);
        _songs.BringToFront();

        var libraryButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 126,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(0, 8, 0, 0),
        };
        libraryButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        libraryButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        libraryButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        libraryButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
        libraryButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34F));
        _importButton.Text = "添加 MIDI";
        _importButton.Dock = DockStyle.Fill;
        _importButton.Click += (_, _) => ImportMidi();
        _audioButton.Text = "音频转 MIDI";
        _audioButton.Dock = DockStyle.Fill;
        _audioButton.Click += (_, _) => CreateMidiFromAudio();
        _jianpuButton.Text = "简谱转 MIDI";
        _jianpuButton.Dock = DockStyle.Fill;
        _jianpuButton.Click += (_, _) => CreateMidiFromJianpu();
        _refreshButton.Text = "刷新";
        _refreshButton.Dock = DockStyle.Fill;
        _refreshButton.Click += (_, _) => RefreshLibrary(GetSelectedSongPath());
        var folderButton = new Button { Text = "打开曲库", Dock = DockStyle.Fill };
        folderButton.Click += (_, _) => OpenSongsFolder();
        libraryButtons.Controls.Add(_importButton, 0, 0);
        libraryButtons.Controls.Add(_audioButton, 1, 0);
        libraryButtons.Controls.Add(_jianpuButton, 0, 1);
        libraryButtons.Controls.Add(_refreshButton, 1, 1);
        libraryButtons.Controls.Add(folderButton, 0, 2);
        libraryButtons.SetColumnSpan(folderButton, 2);
        libraryPanel.Controls.Add(libraryButtons);
        libraryButtons.BringToFront();

        var detailPanel = CreateCard();
        detailPanel.Margin = new Padding(12, 0, 0, 0);
        root.Controls.Add(detailPanel, 1, 1);

        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
        };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailPanel.Controls.Add(detail);

        var title = new Label
        {
            Text = "演奏设置",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 20),
        };
        detail.SetColumnSpan(title, 2);
        detail.Controls.Add(title, 0, 0);

        detail.Controls.Add(CreateFieldLabel("旋律音轨"), 0, 1);
        _tracks.DropDownStyle = ComboBoxStyle.DropDownList;
        _tracks.Dock = DockStyle.Top;
        _tracks.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressTrackChange) RebuildPlan();
        };
        detail.Controls.Add(_tracks, 1, 1);

        detail.Controls.Add(CreateFieldLabel("演奏速度"), 0, 2);
        _speed.DecimalPlaces = 2;
        _speed.Minimum = 0.50M;
        _speed.Maximum = 1.50M;
        _speed.Increment = 0.05M;
        _speed.Value = 1.00M;
        _speed.Width = 120;
        _speed.ValueChanged += (_, _) => RebuildPlan();
        detail.Controls.Add(_speed, 1, 2);

        _songInfo.AutoSize = true;
        _songInfo.ForeColor = Color.FromArgb(70, 75, 85);
        _songInfo.Margin = new Padding(0, 16, 0, 12);
        detail.SetColumnSpan(_songInfo, 2);
        detail.Controls.Add(_songInfo, 0, 3);

        var mapping = new Label
        {
            AutoSize = true,
            Text = "按键：1–7/高音1 = Z X C V B N M ,\n鼠标：左键=降八度　中键=升半音　右键=升八度",
            ForeColor = Color.FromArgb(70, 75, 85),
            Margin = new Padding(0, 4, 0, 16),
        };
        detail.SetColumnSpan(mapping, 2);
        detail.Controls.Add(mapping, 0, 4);

        _hotkeyInfo.AutoSize = true;
        _hotkeyInfo.Text = "F9：立即开始　　F10：紧急停止\n点击下方开始按钮：倒计时 3 秒后演奏";
        _hotkeyInfo.Font = new Font(Font, FontStyle.Bold);
        _hotkeyInfo.ForeColor = Color.FromArgb(25, 90, 160);
        _hotkeyInfo.Margin = new Padding(0, 4, 0, 16);
        detail.SetColumnSpan(_hotkeyInfo, 2);
        detail.Controls.Add(_hotkeyInfo, 0, 5);

        _progress.Dock = DockStyle.Top;
        _progress.Height = 18;
        detail.SetColumnSpan(_progress, 2);
        detail.Controls.Add(_progress, 0, 6);

        _status.Text = "请选择一首 MIDI。";
        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(45, 50, 60);
        _status.Margin = new Padding(0, 12, 0, 12);
        detail.SetColumnSpan(_status, 2);
        detail.Controls.Add(_status, 0, 7);

        var playButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 52,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _playButton.Text = "开始演奏（3 秒后）";
        _playButton.Width = 190;
        _playButton.Height = 40;
        _playButton.BackColor = Color.FromArgb(40, 116, 240);
        _playButton.ForeColor = Color.White;
        _playButton.FlatStyle = FlatStyle.Flat;
        _playButton.Enabled = false;
        _playButton.Click += async (_, _) => await StartPlaybackAsync(3);

        _stopButton.Text = "停止（F10）";
        _stopButton.Width = 130;
        _stopButton.Height = 40;
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopPlayback();
        playButtons.Controls.Add(_playButton);
        playButtons.Controls.Add(_stopButton);
        detail.SetColumnSpan(playButtons, 2);
        detail.Controls.Add(playButtons, 0, 8);

        var footer = new Label
        {
            Text = "只处理本地 MIDI/音频；AI 模型离线运行，不上传内容，也不读写游戏内存。",
            AutoSize = true,
            ForeColor = Color.Gray,
            Padding = new Padding(2, 14, 2, 0),
        };
        root.SetColumnSpan(footer, 2);
        root.Controls.Add(footer, 0, 2);
    }

    private static Panel CreateCard() => new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(16),
        BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 7, 8, 14),
    };

    private void RefreshLibrary(string? selectPath = null)
    {
        var paths = MidiLibrary.FindSongs();
        _songs.BeginUpdate();
        _songs.Items.Clear();
        foreach (var path in paths)
        {
            _songs.Items.Add(new SongListItem(path));
        }
        _songs.EndUpdate();

        if (_songs.Items.Count == 0)
        {
            ClearCurrentSong();
            _status.Text = "曲库为空，请点击“添加 MIDI”。";
            return;
        }

        var selectedIndex = 0;
        if (selectPath is not null)
        {
            for (var i = 0; i < _songs.Items.Count; i++)
            {
                if (_songs.Items[i] is SongListItem item
                    && item.Path.Equals(selectPath, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }
        }
        _songs.SelectedIndex = selectedIndex;
    }

    private void ImportMidi()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 MIDI 文件",
            Filter = "MIDI 文件 (*.mid;*.midi)|*.mid;*.midi",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string? last = null;
        try
        {
            foreach (var file in dialog.FileNames)
            {
                last = MidiLibrary.Import(file);
            }
            RefreshLibrary(last);
            _status.Text = $"已添加 {dialog.FileNames.Length} 个 MIDI 文件。";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void CreateMidiFromJianpu()
    {
        using var dialog = new JianpuForm();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.CreatedPath is null)
        {
            return;
        }

        RefreshLibrary(dialog.CreatedPath);
        _status.Text = $"已生成并加入曲库：{Path.GetFileNameWithoutExtension(dialog.CreatedPath)}";
    }

    private void CreateMidiFromAudio()
    {
        using var dialog = new AudioToMidiForm();
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.CreatedPath is null)
        {
            return;
        }

        RefreshLibrary(dialog.CreatedPath);
        _status.Text = $"AI 扒谱完成并已加入曲库：{Path.GetFileNameWithoutExtension(dialog.CreatedPath)}";
    }

    private void LoadSelectedSong()
    {
        if (_songs.SelectedItem is not SongListItem item)
        {
            ClearCurrentSong();
            return;
        }

        try
        {
            var loadedSong = MidiLibrary.Load(item.Path);
            _suppressTrackChange = true;
            _tracks.BeginUpdate();
            _tracks.Items.Clear();
            foreach (var track in loadedSong.Tracks)
            {
                _tracks.Items.Add(track);
            }
            _tracks.EndUpdate();
            _tracks.SelectedItem = MidiLibrary.PickDefaultTrack(loadedSong);
            _suppressTrackChange = false;
            RebuildPlan();
        }
        catch (Exception ex)
        {
            _suppressTrackChange = false;
            ClearCurrentSong();
            ShowError($"无法读取这首 MIDI：{ex.Message}");
        }
    }

    private void RebuildPlan()
    {
        if (_tracks.SelectedItem is not SongTrack track)
        {
            return;
        }

        try
        {
            _plan = HarmonicaPlanner.Build(track.Notes, (double)_speed.Value);
            var transposeText = _plan.Transpose == 0 ? "不移调" : $"移调 {_plan.Transpose:+#;-#;0} 半音";
            _songInfo.Text = $"{_plan.Notes.Count} 个演奏音符　·　{_plan.Duration:mm\\:ss}　·　{transposeText}"
                + (_plan.DroppedNotes > 0 ? $"　·　舍弃 {_plan.DroppedNotes} 个超范围音符" : string.Empty)
                + (_plan.LeftMouseNotes > 0 ? $"\n其中 {_plan.LeftMouseNotes} 个低音会使用鼠标左键。" : string.Empty);
            _songInfo.ForeColor = Color.FromArgb(70, 75, 85);
            _status.Text = "准备完成。点击开始后有 3 秒切回游戏，或在游戏中按 F9 立即开始。";
            _progress.Value = 0;
            _playButton.Enabled = true;
        }
        catch (Exception ex)
        {
            _plan = null;
            _playButton.Enabled = false;
            ShowError(ex.Message);
        }
    }

    private async Task StartPlaybackAsync(int countdownSeconds)
    {
        if (_plan is null || _player.IsRunning)
        {
            return;
        }

        _playButton.Enabled = false;
        _stopButton.Enabled = true;
        SetSelectionControlsEnabled(false);
        var progress = new Progress<PlaybackStatus>(UpdatePlaybackStatus);
        try
        {
            await _player.PlayAsync(_plan, countdownSeconds, progress);
        }
        catch (Exception ex)
        {
            ShowError($"演奏失败：{ex.Message}");
        }
        finally
        {
            _playButton.Enabled = _plan is not null;
            _stopButton.Enabled = false;
            SetSelectionControlsEnabled(true);
        }
    }

    private void StopPlayback()
    {
        if (!_player.IsRunning)
        {
            if (InputSender.HasHeldInputs)
            {
                var released = InputSender.ReleaseHeld();
                _status.Text = released
                    ? "已重新发送松键指令。"
                    : "仍有按键未能松开，请检查权限后再按 F10。";
            }
            return;
        }
        _player.Stop();
        _status.Text = "正在停止并松开全部按键…";
    }

    private void UpdatePlaybackStatus(PlaybackStatus status)
    {
        _status.Text = status.Text;
        _progress.Maximum = Math.Max(1, status.Total);
        _progress.Value = Math.Clamp(status.Current, 0, _progress.Maximum);
        _stopButton.Enabled = status.Running;
    }

    private void OpenSongsFolder()
    {
        Directory.CreateDirectory(MidiLibrary.SongsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{MidiLibrary.SongsDirectory}\"",
            UseShellExecute = true,
        });
    }

    private void RegisterGlobalHotkeys()
    {
        _startHotkeyRegistered = RegisterHotKey(Handle, HotkeyStartId, ModNoRepeat, (uint)Keys.F9);
        _stopHotkeyRegistered = RegisterHotKey(Handle, HotkeyStopId, ModNoRepeat, (uint)Keys.F10);
        if (!_startHotkeyRegistered || !_stopHotkeyRegistered)
        {
            if (_startHotkeyRegistered)
            {
                UnregisterHotKey(Handle, HotkeyStartId);
                _startHotkeyRegistered = false;
            }
            if (_stopHotkeyRegistered)
            {
                UnregisterHotKey(Handle, HotkeyStopId);
                _stopHotkeyRegistered = false;
            }
            _hotkeyInfo.Text = "⚠ F9 或 F10 被其他程序占用，两个全局热键均未启用。\n仍可使用界面上的开始和停止按钮。";
            _hotkeyInfo.ForeColor = Color.FromArgb(180, 65, 45);
        }
    }

    private string? GetSelectedSongPath() => (_songs.SelectedItem as SongListItem)?.Path;

    private void ClearCurrentSong()
    {
        _plan = null;
        _suppressTrackChange = true;
        _tracks.Items.Clear();
        _suppressTrackChange = false;
        _songInfo.Text = string.Empty;
        _progress.Value = 0;
        _playButton.Enabled = false;
    }

    private void SetSelectionControlsEnabled(bool enabled)
    {
        _songs.Enabled = enabled;
        _tracks.Enabled = enabled;
        _speed.Enabled = enabled;
        _importButton.Enabled = enabled;
        _audioButton.Enabled = enabled;
        _jianpuButton.Enabled = enabled;
        _refreshButton.Enabled = enabled;
    }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_closeAfterPlayback && _player.IsRunning && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _player.Stop();
            _status.Text = "正在停止并安全松开按键…";
            await _player.WaitForStopAsync();
            _closeAfterPlayback = true;
            Close();
            return;
        }

        if (_player.IsRunning) _player.Stop();
        _ = InputSender.ReleaseHeld();
        if (_startHotkeyRegistered) UnregisterHotKey(Handle, HotkeyStartId);
        if (_stopHotkeyRegistered) UnregisterHotKey(Handle, HotkeyStopId);
    }

    private void ShowError(string message)
    {
        _status.Text = message;
        MessageBox.Show(this, message, "三角洲口风琴播放器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
