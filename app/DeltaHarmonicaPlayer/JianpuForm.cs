namespace DeltaHarmonicaPlayer;

internal sealed class JianpuForm : Form
{
    private readonly TextBox _title = new();
    private readonly ComboBox _key = new();
    private readonly NumericUpDown _bpm = new();
    private readonly TextBox _notation = new();
    private readonly Label _status = new();

    public string? CreatedPath { get; private set; }

    public JianpuForm()
    {
        Text = "简谱转 MIDI";
        Width = 720;
        Height = 610;
        MinimumSize = new Size(600, 500);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 247, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 6,
            Margin = new Padding(0, 0, 0, 12),
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        root.Controls.Add(settings, 0, 0);

        settings.Controls.Add(FieldLabel("曲名"), 0, 0);
        _title.Dock = DockStyle.Fill;
        _title.Text = "新简谱";
        settings.Controls.Add(_title, 1, 0);

        settings.Controls.Add(FieldLabel("1 ="), 2, 0);
        _key.DropDownStyle = ComboBoxStyle.DropDownList;
        _key.Dock = DockStyle.Fill;
        _key.Items.AddRange(["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]);
        _key.SelectedIndex = 0;
        settings.Controls.Add(_key, 3, 0);

        settings.Controls.Add(FieldLabel("BPM"), 4, 0);
        _bpm.Minimum = 20;
        _bpm.Maximum = 400;
        _bpm.Value = 120;
        _bpm.Dock = DockStyle.Fill;
        settings.Controls.Add(_bpm, 5, 0);

        var help = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(70, 75, 85),
            Margin = new Padding(0, 0, 0, 10),
            Text = "直接输入连续或空格分组的数字。1–7=音符，0=休止，#4/b7=升降音，1' 或 1̇=高八度，1, 或 1̣=低八度，- =延长一拍，/ =缩短一半，| =小节线。\n截图示例：1'1'1'1'6 56542 | 422422 55655",
        };
        root.Controls.Add(help, 0, 1);

        _notation.AcceptsReturn = true;
        _notation.Multiline = true;
        _notation.ScrollBars = ScrollBars.Vertical;
        _notation.Dock = DockStyle.Fill;
        _notation.Font = new Font("Consolas", 14F);
        _notation.PlaceholderText = "例如：\r\n1 1 5 5 | 6 6 5 -\r\n4 4 3 3 | 2 2 1 -";
        root.Controls.Add(_notation, 0, 2);

        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(180, 65, 45);
        _status.Margin = new Padding(0, 10, 0, 8);
        root.Controls.Add(_status, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var createButton = new Button
        {
            Text = "生成并加入曲库",
            AutoSize = true,
            Height = 38,
            BackColor = Color.FromArgb(40, 116, 240),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        createButton.Click += (_, _) => CreateMidi();
        var cancelButton = new Button { Text = "取消", AutoSize = true, Height = 38 };
        cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(createButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 4);

        AcceptButton = createButton;
        CancelButton = cancelButton;
    }

    private void CreateMidi()
    {
        var title = _title.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            ShowProblem("请填写曲名。", _title);
            return;
        }

        if (string.IsNullOrWhiteSpace(_notation.Text))
        {
            ShowProblem("请粘贴或输入简谱。", _notation);
            return;
        }

        var options = new JianpuOptions(_key.SelectedItem?.ToString() ?? "C", (int)_bpm.Value);
        var result = JianpuConverter.Parse(_notation.Text, options);
        if (!result.Success)
        {
            var first = result.Diagnostics.First(d => d.Severity == JianpuDiagnosticSeverity.Error);
            _status.Text = first.ToString();
            _notation.Focus();
            _notation.SelectionStart = Math.Clamp(first.Position - 1, 0, _notation.TextLength);
            _notation.SelectionLength = _notation.SelectionStart < _notation.TextLength ? 1 : 0;
            return;
        }

        try
        {
            var outputPath = MidiLibrary.CreateUniqueSongPath(title);
            JianpuConverter.WriteMidi(result, outputPath, title);
            CreatedPath = outputPath;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = $"生成失败：{ex.Message}";
        }
    }

    private void ShowProblem(string message, Control focus)
    {
        _status.Text = message;
        focus.Focus();
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 7, 7, 0),
    };
}
