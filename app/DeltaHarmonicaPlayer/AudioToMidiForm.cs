using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeltaHarmonicaPlayer;

internal sealed class AudioToMidiForm : Form
{
    private const string AssetHost = "delta-audio.local";
    private const string WebViewRuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private const int MaximumWebMessageCharacters = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _loading = new()
    {
        Dock = DockStyle.Fill,
        Text = "正在打开离线扒谱工具…",
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(70, 75, 85),
    };
    private readonly bool _closeWhenReadyForSmokeTest;
    private readonly string? _automatedAudioPath;
    private readonly System.Windows.Forms.Timer? _smokeTimeout;
    private bool _saving;
    private bool _initialized;
    private bool _problemShown;
    private bool _forceCloseForAutomation;

    public string? CreatedPath { get; private set; }
    public int SmokeExitCode { get; private set; } = 2;

    public AudioToMidiForm(
        bool closeWhenReadyForSmokeTest = false,
        string? automatedAudioPath = null)
    {
        _closeWhenReadyForSmokeTest = closeWhenReadyForSmokeTest;
        _automatedAudioPath = automatedAudioPath;
        Text = "音频转 MIDI（AI 扒谱）";
        Width = 860;
        Height = 720;
        MinimumSize = new Size(700, 590);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 247, 250);
        Controls.Add(_loading);
        Controls.Add(_webView);
        _webView.Visible = false;

        Shown += async (_, _) => await InitializeAsync();
        FormClosing += HandleFormClosing;

        if (closeWhenReadyForSmokeTest || automatedAudioPath is not null)
        {
            _smokeTimeout = new System.Windows.Forms.Timer { Interval = 30_000 };
            _smokeTimeout.Tick += (_, _) =>
            {
                _smokeTimeout.Stop();
                SmokeExitCode = 2;
                _forceCloseForAutomation = true;
                Close();
            };
            _smokeTimeout.Start();
        }
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (!_saving || DialogResult == DialogResult.OK || _forceCloseForAutomation) return;
        args.Cancel = true;
        MessageBox.Show(
            this,
            "正在把识别结果写入曲库，请稍候几秒。",
            "音频转 MIDI",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task InitializeAsync()
    {
        if (_initialized || IsDisposed) return;
        _initialized = true;

        try
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "AudioTranscriber");
            if (!File.Exists(Path.Combine(assets, "index.html"))
                || !File.Exists(Path.Combine(assets, "app.js"))
                || !File.Exists(Path.Combine(assets, "model", "model.json"))
                || !File.Exists(Path.Combine(assets, "model", "group1-shard1of1.bin")))
            {
                throw new FileNotFoundException("离线扒谱资源不完整，请重新解压或安装播放器。");
            }

            var appDirectory = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var appRoot = Directory.GetParent(appDirectory)?.FullName ?? appDirectory;
            var userDataDirectory = Path.Combine(appRoot, "WebView2Data");
            Directory.CreateDirectory(userDataDirectory);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataDirectory);
            if (IsDisposed) return;
            await _webView.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                AssetHost,
                assets,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += HandleWebMessage;
            core.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith($"https://{AssetHost}/", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess || IsDisposed) return;
                ShowInitializationProblem(
                    "离线扒谱页面加载失败，请关闭窗口后重试。",
                    showInstallButton: false);
            };
            core.ProcessFailed += (_, _) =>
            {
                if (IsDisposed || _saving) return;
                ShowInitializationProblem(
                    "AI 计算组件已停止运行。请关闭这个窗口后重新打开；如果音频较长，可先截取需要的片段。",
                    showInstallButton: false);
            };

            _webView.Visible = true;
            _webView.BringToFront();
            core.Navigate($"https://{AssetHost}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowInitializationProblem(
                "电脑缺少 Microsoft Edge WebView2 组件。安装后重新打开播放器即可使用音频扒谱。",
                showInstallButton: true);
        }
        catch (Exception ex)
        {
            ShowInitializationProblem($"无法打开音频扒谱：{ex.Message}", showInstallButton: false);
        }
    }

    private async void HandleWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_saving || IsDisposed) return;
        if (!args.Source.StartsWith($"https://{AssetHost}/", StringComparison.OrdinalIgnoreCase)) return;

        string json;
        string? messageType;
        try
        {
            json = args.WebMessageAsJson;
            if (json.Length > MaximumWebMessageCharacters)
            {
                await NotifyHostErrorAsync("识别结果过大，请截短音频后重试。");
                return;
            }
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("type", out var typeElement)
                || string.IsNullOrWhiteSpace(messageType = typeElement.GetString()))
            {
                return;
            }
        }
        catch
        {
            return;
        }

        if (messageType.Equals("ready", StringComparison.Ordinal))
        {
            if (_automatedAudioPath is not null)
            {
                await InjectAutomatedAudioAsync(_automatedAudioPath);
            }
            else if (_closeWhenReadyForSmokeTest)
            {
                _smokeTimeout?.Stop();
                SmokeExitCode = 0;
                BeginInvoke(new Action(Close));
            }
            return;
        }
        if (messageType.Equals("error", StringComparison.Ordinal)
            && _automatedAudioPath is not null)
        {
            _smokeTimeout?.Stop();
            SmokeExitCode = 1;
            BeginInvoke(new Action(Close));
            return;
        }
        if (!messageType.Equals("complete", StringComparison.Ordinal)) return;

        _saving = true;
        try
        {
            var request = await Task.Run(() =>
                JsonSerializer.Deserialize<AudioTranscriptionRequest>(json, JsonOptions)
                ?? throw new InvalidDataException("无法读取 AI 识别结果。"));
            var result = await Task.Run(() => AudioMidiWriter.WriteToSongs(request));
            CreatedPath = result.Path;
            SmokeExitCode = 0;
            _smokeTimeout?.Stop();
            await NotifyHostSavedAsync();
            if (IsDisposed) return;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _saving = false;
            if (_automatedAudioPath is not null)
            {
                _smokeTimeout?.Stop();
                SmokeExitCode = 1;
                BeginInvoke(new Action(Close));
                return;
            }
            await NotifyHostErrorAsync(ex.GetBaseException().Message);
        }
    }

    private async Task NotifyHostSavedAsync()
    {
        try
        {
            if (IsDisposed || _webView.CoreWebView2 is null) return;
            await _webView.CoreWebView2.ExecuteScriptAsync("window.audioToMidiHostSaved?.()");
            await Task.Delay(350);
        }
        catch
        {
            // The MIDI is already safely stored. A renderer shutdown must not
            // turn a successful conversion into a false failure or duplicate.
        }
    }

    private async Task NotifyHostErrorAsync(string message)
    {
        try
        {
            if (IsDisposed || _webView.CoreWebView2 is null) return;
            var messageLiteral = JsonSerializer.Serialize(message);
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.audioToMidiHostError?.({messageLiteral})");
        }
        catch
        {
            // The WebView may have been closed or its renderer may have exited.
            // There is no UI left to notify in that case.
        }
    }

    private void ShowInitializationProblem(string message, bool showInstallButton)
    {
        if (_problemShown || IsDisposed) return;
        _problemShown = true;

        if (_closeWhenReadyForSmokeTest || _automatedAudioPath is not null)
        {
            _smokeTimeout?.Stop();
            SmokeExitCode = 1;
            _forceCloseForAutomation = true;
            BeginInvoke(new Action(Close));
            return;
        }

        _webView.Visible = false;
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(40),
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = message,
            ForeColor = Color.FromArgb(160, 55, 45),
            Margin = new Padding(0, 100, 0, 18),
        });
        if (showInstallButton)
        {
            var install = new Button
            {
                Text = "打开微软官方下载页",
                AutoSize = true,
                Height = 40,
            };
            install.Click += (_, _) => Process.Start(new ProcessStartInfo
            {
                FileName = WebViewRuntimeUrl,
                UseShellExecute = true,
            });
            panel.Controls.Add(install);
        }
        Controls.Add(panel);
        panel.BringToFront();
    }

    private async Task InjectAutomatedAudioAsync(string audioPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(audioPath);
            var bytes = await File.ReadAllBytesAsync(fullPath);
            var base64Literal = JsonSerializer.Serialize(Convert.ToBase64String(bytes));
            var nameLiteral = JsonSerializer.Serialize(Path.GetFileName(fullPath));
            var script = $$"""
                (() => {
                  const binary = atob({{base64Literal}});
                  const bytes = new Uint8Array(binary.length);
                  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
                  const transfer = new DataTransfer();
                  transfer.items.add(new File([bytes], {{nameLiteral}}, { type: "audio/wav" }));
                  const input = document.querySelector("#fileInput");
                  input.files = transfer.files;
                  input.dispatchEvent(new Event("change", { bubbles: true }));
                  document.querySelector("#convert").click();
                })()
                """;
            await _webView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
            _smokeTimeout?.Stop();
            SmokeExitCode = 1;
            BeginInvoke(new Action(Close));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _smokeTimeout?.Dispose();
        base.Dispose(disposing);
    }
}
