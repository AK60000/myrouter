using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using myrouter.Models;
using myrouter.Services;

namespace myrouter.Forms;

public class MainForm : Form
{
    private readonly ProxyServer _proxy = new();

    private readonly TextBox _txtUpstream = new();
    private readonly TextBox _txtUpstreamKey = new();
    private readonly CheckBox _chkShowUpstreamKey = new();
    private readonly NumericUpDown _numPort = new();
    private readonly NumericUpDown _numTimeout = new();
    private readonly CheckBox _chkAuth = new();
    private readonly TextBox _txtKey = new();
    private readonly CheckBox _chkShowKey = new();
    private readonly CheckBox _chkLog = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly Button _btnSave = new();
    private readonly Label _lblStatus = new();
    private readonly TextBox _txtLog = new();
    private readonly Button _btnClearLog = new();

    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _menuShow = new();
    private readonly ToolStripMenuItem _menuStart = new();
    private readonly ToolStripMenuItem _menuStop = new();
    private readonly ToolStripMenuItem _menuExit = new();
    private bool _reallyExit;

    public MainForm()
    {
        Text = "myrouter";
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(760, 800);
        MinimumSize = new Size(720, 780);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        Icon = LoadAppIcon();

        BuildLayout();
        InitTray();
        LoadConfig();

        _proxy.Log += msg =>
        {
            if (IsDisposed) return;
            if (InvokeRequired)
                BeginInvoke(() => AppendLog(msg));
            else
                AppendLog(msg);
        };

        FormClosing += (_, e) =>
        {
            if (_reallyExit)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _proxy.Dispose();
                return;
            }
            // 关窗 = 隐藏到托盘（服务继续跑）
            e.Cancel = true;
            Hide();
            if (!_tray.Visible)
            {
                _tray.Visible = true;
                _tray.ShowBalloonTip(2000, "myrouter", "已隐藏到托盘，服务继续运行", ToolTipIcon.Info);
            }
        };
    }

    private void InitTray()
    {
        _menuShow.Text = "显示窗口";
        _menuShow.Click += (_, _) => ShowMainWindow();
        _menuStart.Text = "▶ 启动";
        _menuStart.Click += (_, _) => BtnStart_Click(this, EventArgs.Empty);
        _menuStop.Text = "■ 停止";
        _menuStop.Click += (_, _) => BtnStop_Click(this, EventArgs.Empty);
        _menuExit.Text = "退出";
        _menuExit.Click += (_, _) => ExitApp();

        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            _menuShow,
            new ToolStripSeparator(),
            _menuStart,
            _menuStop,
            new ToolStripSeparator(),
            _menuExit,
        });

        _tray.Icon = LoadAppIcon();
        _tray.Text = "myrouter";
        _tray.Visible = false;
        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        UpdateTrayMenu();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        _tray.Visible = false;
    }

    private void ExitApp()
    {
        if (_proxy.IsRunning)
        {
            var r = MessageBox.Show("服务正在运行，确定退出？", "myrouter",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
        }
        SaveConfigSilent();
        _reallyExit = true;
        Close();
    }

    private void UpdateTrayMenu()
    {
        var running = _proxy.IsRunning;
        _menuStart.Enabled = !running;
        _menuStop.Enabled = running;
    }

    private static Icon LoadAppIcon()
    {
        var asm = typeof(MainForm).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("myrouter.ico", StringComparison.OrdinalIgnoreCase));
        if (name is null) return SystemIcons.Application;
        using var stream = asm.GetManifestResourceStream(name);
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    private void BuildLayout()
    {
        var padding = 16;
        var labelW = 88;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(padding),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // ── 配置区 ──
        var cfg = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 9,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelW));
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        cfg.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Row 0: 上游地址
        cfg.Controls.Add(MakeLabel("上游地址"), 0, 0);
        _txtUpstream.Dock = DockStyle.Fill;
        _txtUpstream.PlaceholderText = AppConfig.DefaultUpstreamUrl;
        _txtUpstream.Margin = new Padding(0, 4, 0, 4);
        cfg.Controls.Add(_txtUpstream, 1, 0);
        cfg.SetColumnSpan(_txtUpstream, 2);

        // Row 1: 上游密钥
        cfg.Controls.Add(MakeLabel("上游密钥"), 0, 1);
        _txtUpstreamKey.Dock = DockStyle.Fill;
        _txtUpstreamKey.UseSystemPasswordChar = true;
        _txtUpstreamKey.PlaceholderText = "空 = 透传客户端 Authorization";
        _txtUpstreamKey.Margin = new Padding(0, 4, 0, 4);
        cfg.Controls.Add(_txtUpstreamKey, 1, 1);
        _chkShowUpstreamKey.Text = "显示";
        _chkShowUpstreamKey.AutoSize = true;
        _chkShowUpstreamKey.Margin = new Padding(8, 4, 0, 4);
        WireShowPasswordToggle(_txtUpstreamKey, _chkShowUpstreamKey);
        cfg.Controls.Add(_chkShowUpstreamKey, 2, 1);

        // Row 2: 端口
        cfg.Controls.Add(MakeLabel("端口"), 0, 2);
        _numPort.Dock = DockStyle.Left;
        _numPort.Minimum = AppConfig.MinPort;
        _numPort.Maximum = AppConfig.MaxPort;
        _numPort.Value = AppConfig.DefaultPort;
        _numPort.Width = 120;
        _numPort.Margin = new Padding(0, 4, 0, 4);
        cfg.Controls.Add(_numPort, 1, 2);

        // Row 3: 上游超时
        cfg.Controls.Add(MakeLabel("超时"), 0, 3);
        var timeoutPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 4),
        };
        _numTimeout.Minimum = AppConfig.MinUpstreamTimeoutSeconds;
        _numTimeout.Maximum = AppConfig.MaxUpstreamTimeoutSeconds;
        _numTimeout.Value = AppConfig.DefaultUpstreamTimeoutSeconds;
        _numTimeout.Width = 120;
        timeoutPanel.Controls.Add(_numTimeout);
        var lblTimeoutUnit = new Label
        {
            Text = "秒",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(8, 5, 0, 0),
        };
        timeoutPanel.Controls.Add(lblTimeoutUnit);
        cfg.Controls.Add(timeoutPanel, 1, 3);

        // Row 4: 鉴权 checkbox
        cfg.Controls.Add(MakeLabel(""), 0, 4);
        _chkAuth.Text = "启用鉴权（校验本地 API Key）";
        _chkAuth.AutoSize = true;
        _chkAuth.Margin = new Padding(0, 8, 0, 4);
        _chkAuth.CheckedChanged += (_, _) => UpdateAuthEnabled();
        cfg.Controls.Add(_chkAuth, 1, 4);
        cfg.SetColumnSpan(_chkAuth, 2);

        // Row 5: 密钥
        cfg.Controls.Add(MakeLabel("密钥"), 0, 5);
        _txtKey.Dock = DockStyle.Fill;
        _txtKey.UseSystemPasswordChar = true;
        _txtKey.PlaceholderText = "客户端用此 Key 访问本地服务";
        _txtKey.Margin = new Padding(0, 4, 0, 4);
        cfg.Controls.Add(_txtKey, 1, 5);
        _chkShowKey.Text = "显示";
        _chkShowKey.AutoSize = true;
        _chkShowKey.Margin = new Padding(8, 4, 0, 4);
        WireShowPasswordToggle(_txtKey, _chkShowKey);
        cfg.Controls.Add(_chkShowKey, 2, 5);

        // Row 6: 记录请求 checkbox + hint
        cfg.Controls.Add(MakeLabel(""), 0, 6);
        _chkLog.Text = "记录每个请求";
        _chkLog.AutoSize = true;
        _chkLog.Margin = new Padding(0, 4, 0, 4);
        cfg.Controls.Add(_chkLog, 1, 6);
        var lblLogHint = new Label
        {
            Text = "（生产环境建议关闭，影响性能）",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(8, 6, 0, 4),
        };
        cfg.Controls.Add(lblLogHint, 2, 6);

        // Row 7: separator
        var sep = new Panel { Dock = DockStyle.Top, Height = 10 };
        cfg.Controls.Add(sep, 0, 7);
        cfg.SetColumnSpan(sep, 3);

        // Row 8: buttons
        var btns = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        btns.Margin = new Padding(0, 4, 0, 0);
        _btnStart.Text = "▶ 启动";
        _btnStart.Size = new Size(100, 40);
        _btnStart.Click += BtnStart_Click;
        _btnStop.Text = "■ 停止";
        _btnStop.Size = new Size(100, 40);
        _btnStop.Enabled = false;
        _btnStop.Click += BtnStop_Click;
        _btnSave.Text = "💾 保存配置";
        _btnSave.Size = new Size(120, 40);
        _btnSave.Click += (_, _) => SaveConfigWithFeedback();
        btns.Controls.AddRange(new Control[] { _btnStart, _btnStop, _btnSave });
        cfg.Controls.Add(btns, 0, 8);
        cfg.SetColumnSpan(btns, 3);

        root.Controls.Add(cfg, 0, 0);

        // ── 状态区 ──
        var statusPanel = new Panel { Dock = DockStyle.Fill };
        _lblStatus.AutoSize = false;
        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.Text = "状态: 已停止";
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.Padding = new Padding(4, 0, 4, 0);
        statusPanel.Controls.Add(_lblStatus);
        root.Controls.Add(statusPanel, 0, 1);

        // ── 日志区 ──
        var logPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
        };
        logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var logHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logHeader.Padding = new Padding(4, 8, 4, 8);
        var lblLog = new Label
        {
            Text = "日志",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };
        logHeader.Controls.Add(lblLog, 0, 0);
        logHeader.SetColumnSpan(lblLog, 1);
        _btnClearLog.Text = "清空";
        _btnClearLog.Size = new Size(72, 39);
        _btnClearLog.Margin = Padding.Empty;
        logHeader.Controls.Add(_btnClearLog, 1, 0);
        _btnClearLog.Click += (_, _) => _txtLog.Clear();
        logPanel.Controls.Add(logHeader, 0, 0);

        _txtLog.Multiline = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.ReadOnly = true;
        _txtLog.BackColor = Color.FromArgb(20, 20, 20);
        _txtLog.ForeColor = Color.FromArgb(220, 220, 220);
        _txtLog.Font = new Font("Consolas", 9f);
        _txtLog.Dock = DockStyle.Fill;
        logPanel.Controls.Add(_txtLog, 0, 1);

        root.Controls.Add(logPanel, 0, 2);

        Controls.Add(root);
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleRight,
        Margin = new Padding(0, 6, 8, 0),
    };

    private static void WireShowPasswordToggle(TextBox box, CheckBox chk) =>
        chk.CheckedChanged += (_, _) => box.UseSystemPasswordChar = !chk.Checked;

    private void LoadConfig()
    {
        var c = AppConfig.Load();
        _txtUpstream.Text = c.UpstreamUrl;
        _txtUpstreamKey.Text = c.UpstreamApiKey;
        _numPort.Value = Math.Clamp(c.Port, AppConfig.MinPort, AppConfig.MaxPort);
        _numTimeout.Value = Math.Clamp(c.UpstreamTimeoutSeconds,
            AppConfig.MinUpstreamTimeoutSeconds, AppConfig.MaxUpstreamTimeoutSeconds);
        _chkAuth.Checked = c.RequireAuth;
        _txtKey.Text = c.ApiKey;
        _chkLog.Checked = c.LogRequests;
        UpdateAuthEnabled();
    }

    private AppConfig CurrentConfig() => new()
    {
        UpstreamUrl = _txtUpstream.Text.Trim(),
        UpstreamApiKey = _txtUpstreamKey.Text.Trim(),
        Port = (int)_numPort.Value,
        UpstreamTimeoutSeconds = (int)_numTimeout.Value,
        RequireAuth = _chkAuth.Checked,
        ApiKey = _txtKey.Text.Trim(),
        LogRequests = _chkLog.Checked,
    };

    private void SaveConfigSilent()
    {
        try { CurrentConfig().Save(); }
        catch { /* 静默失败 - 退出时不影响 */ }
    }

    private void SaveConfigWithFeedback()
    {
        try
        {
            CurrentConfig().Save();
            AppendLog("[配置已保存]");
            MessageBox.Show("配置已保存", "myrouter",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateAuthEnabled()
    {
        _txtKey.Enabled = _chkAuth.Checked;
        _chkShowKey.Enabled = _chkAuth.Checked;
    }

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        _btnStart.Enabled = false;
        try
        {
            await _proxy.StartAsync(CurrentConfig());
            SaveConfigSilent();
            _lblStatus.Text = $"状态: 运行中 → http://localhost:{_numPort.Value}";
            _lblStatus.ForeColor = Color.FromArgb(0, 130, 0);
            _btnStop.Enabled = true;
            UpdateTrayMenu();
            AppendLog($"[GUI] 启动成功，监听 {_numPort.Value}");
        }
        catch (Exception ex)
        {
            _btnStart.Enabled = true;
            AppendLog($"[GUI] 启动失败: {ex.Message}");
            MessageBox.Show(ex.Message, "启动失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void BtnStop_Click(object? sender, EventArgs e)
    {
        _btnStop.Enabled = false;
        try
        {
            await _proxy.StopAsync();
            _lblStatus.Text = "状态: 已停止";
            _lblStatus.ForeColor = Color.FromArgb(160, 0, 0);
            _btnStart.Enabled = true;
            UpdateTrayMenu();
            AppendLog("[GUI] 已停止");
        }
        catch (Exception ex)
        {
            _btnStop.Enabled = true;
            AppendLog($"[GUI] 停止失败: {ex.Message}");
        }
    }

    private void AppendLog(string line)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        _txtLog.AppendText($"{ts}  {line}{Environment.NewLine}");
    }
}
