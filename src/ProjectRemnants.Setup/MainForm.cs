using System.Diagnostics;
using System.Drawing.Drawing2D;
using ProjectRemnants.Setup.Install;
using ProjectRemnants.Setup.Llm;

namespace ProjectRemnants.Setup;

public sealed class MainForm : Form
{
    private readonly TextBox _configPath = new();
    private readonly TextBox _agentPath = new();
    private readonly Label _setupStatus = new();
    private readonly TextBox _userDirectory = new();
    private readonly ComboBox _provider = new();
    private readonly ComboBox _model = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _apiKey = new();
    private readonly Label _gpuStatus = new();
    private readonly Label _modelTip = new();
    private Label? _endpointLabel;
    private Label? _apiKeyLabel;
    private FlowLayoutPanel? _modelTrailing;
    private readonly Button _browseCatalog = new();
    private readonly Button _checkModels = new();
    private IReadOnlyList<string> _ollamaModelChoices = [];
    private IReadOnlyList<string> _remoteModelChoices = [];
    private string _ollamaSelection = "llama3.2:3b";
    private string _remoteSelection = string.Empty;
    private int _lastProviderIndex;
    private readonly Button _configureLlm = new();
    private readonly Button _disableLlm = new();
    private readonly TableLayoutPanel _progressPanel = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _progressStatus = new();
    private readonly TextBox _activity = new();
    private FlowLayoutPanel? _footer;
    private Button? _githubButton;
    private Button? _kofiButton;
    private LinkLabel? _kofiMessage;
    private readonly NotifyIcon _trayIcon = new();
    private readonly ToolTip _toolTip = new();
    private readonly OllamaDriver _ollama = new();
    private readonly RemoteApiBridge _bridge = new();
    private bool _allowClose;
    private bool _checkingOllama;
    private bool _llmPageActive;
    private bool _refreshingModels;

    public MainForm()
    {
        Text = "Project Remnants Setup";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(6F, 13F);
        ClientSize = new Size(720, 560);
        MinimumSize = new Size(620, 520);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        BackColor = Color.FromArgb(245, 246, 248);
        Icon = LoadAppIcon();

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        tabs.TabPages.Add(BuildSetupPage());
        tabs.TabPages.Add(BuildLlmPage());
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            _llmPageActive = tabs.SelectedIndex == 1;
            if (_llmPageActive && _provider.SelectedIndex == 0)
            {
                await InitializeOllamaAsync();
            }
        };
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(tabs, 0, 0);
        shell.Controls.Add(BuildFooter(), 0, 1);
        Controls.Add(shell);

        BuildTrayMenu();
        Shown += (_, _) => DetectInstall();
        Resize += (_, _) => MinimizeToTray();
        FormClosing += HandleClosing;
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        ApplyFooterScale();
    }

    protected override void OnDpiChangedAfterParent(EventArgs eventArgs)
    {
        base.OnDpiChangedAfterParent(eventArgs);
        ApplyFooterScale();
    }

    private Control BuildFooter()
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 4, 8, 4),
            Margin = Padding.Empty,
            BackColor = Color.FromArgb(238, 240, 244)
        };
        _githubButton = CreateIconButton(
            "Open the Project Remnants Setup source on GitHub",
            "https://github.com/Patricklumowa/project-remnants-setup");
        _kofiButton = CreateIconButton(
            "Support Project Remnants on Ko-fi",
            "https://ko-fi.com/patricius");
        footer.Controls.Add(_githubButton);
        footer.Controls.Add(_kofiButton);

        _kofiMessage = new LinkLabel
        {
            AutoSize = true,
            LinkBehavior = LinkBehavior.NeverUnderline,
            LinkColor = Color.FromArgb(41, 171, 224),
            ActiveLinkColor = Color.FromArgb(24, 128, 173),
            VisitedLinkColor = Color.FromArgb(41, 171, 224),
            Text = "help me eat gng\U0001F62D\U0001F62D",
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 0, 0, 0)
        };
        _kofiMessage.LinkClicked += (_, _) => OpenUrl("https://ko-fi.com/patricius");
        _toolTip.SetToolTip(_kofiMessage, "Support Project Remnants on Ko-fi");
        footer.Controls.Add(_kofiMessage);
        _footer = footer;
        return footer;
    }

    private void ApplyFooterScale()
    {
        if (_footer is null)
        {
            return;
        }

        var buttonSize = ScaleLogical(28);
        var inset = new Padding(ScaleLogical(4), ScaleLogical(3), ScaleLogical(4), ScaleLogical(3));
        _footer.Padding = inset;
        foreach (var button in new[] { _githubButton, _kofiButton })
        {
            if (button is null)
            {
                continue;
            }

            button.Size = new Size(buttonSize, buttonSize);
            button.Image?.Dispose();
            button.Image = ReferenceEquals(button, _githubButton)
                ? CreateGitHubIcon()
                : CreateKofiIcon();
        }
    }

    private Button CreateIconButton(string description, string url)
    {
        var button = new Button
        {
            AccessibleName = description,
            Cursor = Cursors.Hand,
            FlatStyle = FlatStyle.Flat,
            ImageAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 6, 0),
            Padding = Padding.Empty,
            TabStop = true
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 224, 231);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(207, 212, 220);
        button.Click += (_, _) => OpenUrl(url);
        _toolTip.SetToolTip(button, description);
        return button;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private const float IconLogicalSize = 20F;

    private int ScaleLogical(float logical) =>
        (int)Math.Round(logical * DeviceDpi / 96.0);

    private Bitmap CreateGitHubIcon()
    {
        var pixels = ScaleLogical(IconLogicalSize);
        var image = new Bitmap(pixels, pixels);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(pixels / IconLogicalSize, pixels / IconLogicalSize);
        using var brush = new SolidBrush(Color.FromArgb(31, 35, 40));
        graphics.FillPolygon(brush, [
            new PointF(3.5F, 7), new PointF(3.2F, 2.5F), new PointF(7.2F, 4.8F),
            new PointF(10, 4.2F), new PointF(12.8F, 4.8F), new PointF(16.8F, 2.5F),
            new PointF(16.5F, 7)
        ]);
        graphics.FillEllipse(brush, 3, 4, 14, 14);
        using var pen = new Pen(brush, 2.2F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawBezier(pen, 5, 14, 2, 14, 3, 18, 1, 18);
        return image;
    }

    private Bitmap CreateKofiIcon()
    {
        var pixels = ScaleLogical(IconLogicalSize);
        var image = new Bitmap(pixels, pixels);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.ScaleTransform(pixels / IconLogicalSize, pixels / IconLogicalSize);
        using var blue = new SolidBrush(Color.FromArgb(41, 171, 224));
        using var white = new SolidBrush(Color.White);
        using var red = new SolidBrush(Color.FromArgb(255, 94, 91));
        graphics.FillEllipse(blue, 1, 1, 18, 18);
        graphics.FillRectangle(white, 4, 6, 10, 8);
        using var handle = new Pen(Color.White, 2);
        graphics.DrawArc(handle, 11, 7, 6, 6, -80, 170);
        graphics.FillEllipse(red, 6, 8, 4, 4);
        graphics.FillEllipse(red, 8, 8, 4, 4);
        graphics.FillPolygon(red, [new PointF(6.2F, 10), new PointF(11.8F, 10), new PointF(9, 13)]);
        return image;
    }

    private TabPage BuildSetupPage()
    {
        var page = new TabPage("Game Setup") { BackColor = BackColor, Padding = new Padding(16) };
        var layout = CreatePageLayout();
        AddHeading(layout, "Java agent installation");

        AddPathRow(layout, 1, "Game config", _configPath, BrowseConfig);
        AddPathRow(layout, 2, "NPCFW.jar", _agentPath, BrowseAgent);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 10, 0, 5),
            Anchor = AnchorStyles.Left
        };
        actions.Controls.Add(CreateButton("Install / Repair", InstallAgent, true));
        actions.Controls.Add(CreateButton("Detect Again", (_, _) => DetectInstall()));
        layout.Controls.Add(actions, 1, 3);

        var uninstall = CreateButton("Full Uninstall", Uninstall, false);
        uninstall.ForeColor = Color.Firebrick;
        uninstall.FlatAppearance.BorderColor = Color.FromArgb(190, 90, 90);
        uninstall.Margin = new Padding(0, 12, 0, 4);
        uninstall.Anchor = AnchorStyles.Left;
        layout.Controls.Add(uninstall, 1, 4);

        _setupStatus.AutoSize = true;
        _setupStatus.ForeColor = Color.FromArgb(70, 76, 86);
        _setupStatus.Margin = new Padding(0, 8, 0, 0);
        layout.Controls.Add(_setupStatus, 1, 5);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildLlmPage()
    {
        var page = new TabPage("LLM Companion") { BackColor = BackColor, Padding = new Padding(16) };
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
        var layout = CreateScrollablePageLayout();
        host.ClientSizeChanged += (_, _) =>
            layout.Width = host.ClientSize.Width - (layout.Left + layout.Margin.Horizontal);
        AddHeading(layout, "Optional conversation provider");

        AddPathRow(layout, 1, "Zomboid folder", _userDirectory, BrowseUserDirectory);

        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        _provider.Items.AddRange(["Local Ollama", "OpenAI-compatible API"]);
        _provider.SelectedIndex = 0;
        _provider.SelectedIndexChanged += ProviderChanged;
        AddField(layout, 2, "Provider", _provider);

        _model.DropDownStyle = ComboBoxStyle.DropDown;
        _model.Text = "llama3.2:3b";
        ConfigureButton(_browseCatalog, "Browse catalogue...", BrowseCatalog);
        _browseCatalog.Margin = new Padding(8, 0, 0, 0);
        ConfigureButton(_checkModels, "Check availability", CheckRemoteModels);
        _checkModels.Margin = new Padding(8, 0, 0, 0);
        _toolTip.SetToolTip(
            _checkModels, "Fetch the models this endpoint and API key can use");
        _modelTrailing = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        _modelTrailing.Controls.Add(_browseCatalog);
        _modelTrailing.Controls.Add(_checkModels);
        AddField(layout, 3, "Model", _model, _modelTrailing);

        _gpuStatus.AutoSize = true;
        _gpuStatus.ForeColor = Color.FromArgb(70, 76, 86);
        _gpuStatus.Margin = new Padding(0, 5, 0, 4);
        AddField(layout, 4, "GPU (Ollama)", _gpuStatus);

        _modelTip.AutoSize = true;
        _modelTip.MaximumSize = new Size(560, 0);
        _modelTip.ForeColor = Color.FromArgb(30, 120, 72);
        _modelTip.Font = new Font("Segoe UI", 9F, FontStyle.Italic);
        _modelTip.Margin = new Padding(0, 2, 0, 6);
        _modelTip.Visible = false;
        layout.Controls.Add(_modelTip, 1, 5);
        layout.SetColumnSpan(_modelTip, 2);

        _endpoint.Text = "https://api.openai.com/v1";
        _endpointLabel = AddField(layout, 6, "Endpoint", _endpoint);

        _apiKey.UseSystemPasswordChar = true;
        _apiKeyLabel = AddField(layout, 7, "API key", _apiKey);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 4),
            Anchor = AnchorStyles.Left
        };
        ConfigureButton(_configureLlm, "Configure and Start", ConfigureLlm, true);
        ConfigureButton(_disableLlm, "Disable", DisableLlm);
        actions.Controls.Add(_configureLlm);
        actions.Controls.Add(_disableLlm);
        layout.Controls.Add(actions, 1, 8);

        _progressPanel.Dock = DockStyle.Fill;
        _progressPanel.AutoSize = true;
        _progressPanel.ColumnCount = 2;
        _progressPanel.RowCount = 1;
        _progressPanel.Margin = new Padding(0, 5, 0, 5);
        _progressPanel.Visible = false;
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        _progress.Dock = DockStyle.Fill;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Margin = new Padding(0, 3, 8, 3);
        _progressStatus.AutoEllipsis = true;
        _progressStatus.Dock = DockStyle.Fill;
        _progressStatus.TextAlign = ContentAlignment.MiddleLeft;
        _progressStatus.ForeColor = Color.FromArgb(70, 76, 86);
        _progressPanel.Controls.Add(_progress, 0, 0);
        _progressPanel.Controls.Add(_progressStatus, 1, 0);
        layout.Controls.Add(_progressPanel, 1, 9);

        _activity.Dock = DockStyle.Fill;
        _activity.Multiline = true;
        _activity.ReadOnly = true;
        _activity.ScrollBars = ScrollBars.Both;
        _activity.WordWrap = false;
        _activity.BackColor = Color.White;
        _activity.Font = new Font("Consolas", 9F);
        _activity.Height = 240;
        _activity.Margin = new Padding(0, 4, 0, 0);
        layout.Controls.Add(_activity, 1, 10);
        layout.SetColumnSpan(_activity, 2);
        host.Controls.Add(layout);
        page.Controls.Add(host);

        _userDirectory.Text = LlmConfigurationStore.DefaultUserDirectory;
        LoadSavedLlmSettings();
        UpdateProviderFields();
        _ = RefreshGpuStatusAsync();
        return page;
    }

    private static TableLayoutPanel CreatePageLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 3,
            RowCount = 10
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var row = 0; row < 9; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return layout;
    }

    private static TableLayoutPanel CreateScrollablePageLayout()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 11
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var row = 0; row < 10; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 240F));
        return layout;
    }

    private static void AddHeading(TableLayoutPanel layout, string title)
    {
        var heading = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 14F),
            Text = title,
            ForeColor = Color.FromArgb(34, 39, 48),
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 3);
    }

    private static void AddPathRow(
        TableLayoutPanel layout, int row, string label, TextBox textBox, EventHandler browse)
    {
        layout.Controls.Add(CreateLabel(label), 0, row);
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 2, 0, 0);
        layout.Controls.Add(textBox, 1, row);
        var button = CreateButton("Browse", browse);
        button.Margin = new Padding(8, 0, 0, 0);
        layout.Controls.Add(button, 2, row);
    }

    private static Label AddField(
        TableLayoutPanel layout, int row, string label, Control control, Control? trailing = null)
    {
        var labelControl = CreateLabel(label);
        layout.Controls.Add(labelControl, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 2, 0, 0);
        layout.Controls.Add(control, 1, row);
        if (trailing is not null)
        {
            trailing.Anchor = AnchorStyles.Left;
            trailing.Margin = new Padding(8, 1, 0, 0);
            layout.Controls.Add(trailing, 2, row);
        }

        return labelControl;
    }

    private static Label CreateLabel(string text) => new()
    {
        AutoSize = true,
        Text = text,
        Font = new Font("Segoe UI Semibold", 9F),
        ForeColor = Color.FromArgb(52, 58, 68),
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 5, 12, 0)
    };

    private static Button CreateButton(
        string text, EventHandler handler, bool primary = false)
    {
        var button = new Button();
        ConfigureButton(button, text, handler, primary);
        return button;
    }

    private static void ConfigureButton(
        Button button, string text, EventHandler handler, bool primary = false)
    {
        button.AutoSize = true;
        button.Padding = new Padding(9, 3, 9, 3);
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary
            ? Color.FromArgb(48, 99, 194)
            : Color.FromArgb(170, 175, 184);
        button.BackColor = primary ? Color.FromArgb(52, 105, 204) : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(45, 50, 58);
        button.Click += handler;
    }

    private void DetectInstall()
    {
        var installs = GameLocator.FindAll();
        if (installs.Count != 0)
        {
            _configPath.Text = installs[0].ConfigPath;
        }

        _agentPath.Text = AgentLocator.Find(
            gameDirectories: installs.Select(install => install.Directory)) ?? _agentPath.Text;
        RefreshSetupStatus();
    }

    private void RefreshSetupStatus()
    {
        try
        {
            if (!File.Exists(_configPath.Text))
            {
                _setupStatus.Text = "Project Zomboid was not detected. Select ProjectZomboid64.json.";
                _setupStatus.ForeColor = Color.DarkGoldenrod;
                return;
            }

            var status = ProjectZomboidConfig.Inspect(_configPath.Text);
            _setupStatus.Text = status.Installed
                ? $"Installed: {status.AgentPath}"
                : status.ObsoleteEntries != 0
                    ? "An obsolete NPCFW loader entry was found. Install / Repair will replace it."
                    : "NPCFW Java agent is not installed.";
            _setupStatus.ForeColor = status.Installed
                ? Color.FromArgb(30, 120, 72)
                : Color.FromArgb(90, 96, 106);
        }
        catch (Exception exception)
        {
            _setupStatus.Text = exception.Message;
            _setupStatus.ForeColor = Color.Firebrick;
        }
    }

    private void InstallAgent(object? sender, EventArgs eventArgs) =>
        RunInstallerMutation(
            () => ProjectZomboidConfig.Install(_configPath.Text, _agentPath.Text),
            "--install", _configPath.Text, _agentPath.Text);

    private async void Uninstall(object? sender, EventArgs eventArgs)
    {
        var answer = MessageBox.Show(
            this,
            "Restore the original game JSON and permanently remove all extra Project Remnants DLLs, backups, settings, logs, local mod files, app-managed Ollama CLI files, and NPC data from every Zomboid save? Downloaded models and your Steam Workshop subscription are unchanged. This cannot be undone.",
            "Uninstall Project Remnants",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        var button = sender as Button;
        if (button is not null)
        {
            button.Enabled = false;
        }

        SetBusy(true);
        try
        {
            await _bridge.StopAsync();
            await _ollama.UnloadModelAsync(_model.Text);
            await _ollama.StopOwnedServerAsync();
            if (!RunInstallerMutation(
                () => ProjectZomboidConfig.Uninstall(_configPath.Text),
                "--uninstall", _configPath.Text))
            {
                return;
            }

            LlmConfigurationStore.Uninstall(_userDirectory.Text);
            _ollama.UninstallPortable();
            _apiKey.Clear();
            RefreshSetupStatus();
            ShowTrayIfNeeded();
            Log("Project Remnants setup was fully uninstalled.");
            MessageBox.Show(
                this,
                "The game JSON was restored and all extra installation files, settings, logs, local mod files, app-managed Ollama CLI files, and save data were removed. Downloaded models and your Steam Workshop subscription were not changed.",
                "Uninstall complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
            if (button is not null)
            {
                button.Enabled = true;
            }
        }
    }

    private bool RunInstallerMutation(Action mutation, params string[] elevatedArguments)
    {
        try
        {
            mutation();
            RefreshSetupStatus();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            if (!Elevation.Run(elevatedArguments))
            {
                ShowError("Administrator access was cancelled or the setup operation failed.");
                return false;
            }

            RefreshSetupStatus();
            return true;
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            return false;
        }
    }

    private void BrowseConfig(object? sender, EventArgs eventArgs)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Project Zomboid config|ProjectZomboid64.json|JSON files|*.json",
            CheckFileExists = true,
            FileName = "ProjectZomboid64.json"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _configPath.Text = dialog.FileName;
            RefreshSetupStatus();
        }
    }

    private void BrowseAgent(object? sender, EventArgs eventArgs)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "NPCFW Java agent|NPCFW.jar|Java archives|*.jar",
            CheckFileExists = true,
            FileName = "NPCFW.jar"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _agentPath.Text = dialog.FileName;
        }
    }

    private async void BrowseCatalog(object? sender, EventArgs eventArgs)
    {
        if (_provider.SelectedIndex != 0)
        {
            return;
        }

        _browseCatalog.Enabled = false;
        try
        {
            var (vram, ram) = await Task.Run(() =>
                (OllamaDriver.DetectVramGb(), OllamaDriver.DetectSystemRamGb()));
            using var dialog = new ModelCatalogForm(_ollama, vram, ram);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedModel is { } model)
            {
                _ollamaSelection = model;
                _model.Text = model;
                Log($"Selected model from catalogue: {model}");
            }
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            _browseCatalog.Enabled = true;
        }
    }

    private void BrowseUserDirectory(object? sender, EventArgs eventArgs)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Project Zomboid user directory",
            InitialDirectory = _userDirectory.Text,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _userDirectory.Text = dialog.SelectedPath;
            LoadSavedLlmSettings();
            _ = RefreshOllamaModelsAsync();
        }
    }

    private async void ProviderChanged(object? sender, EventArgs eventArgs)
    {
        UpdateProviderFields();
        if (_llmPageActive && _provider.SelectedIndex == 0)
        {
            await InitializeOllamaAsync();
        }
    }

    private async Task InitializeOllamaAsync()
    {
        if (_checkingOllama)
        {
            return;
        }

        _checkingOllama = true;
        SetBusy(true);
        try
        {
            if (await EnsureOllamaInstalledAsync())
            {
                await RefreshOllamaModelsAsync();
            }
        }
        catch (Exception exception)
        {
            Log($"Error: {exception.Message}");
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
            _checkingOllama = false;
        }
    }

    private async Task<bool> EnsureOllamaInstalledAsync()
    {
        var executable = _ollama.FindExecutable();
        if (executable is not null)
        {
            Log($"Ollama found: {executable}");
            return true;
        }

        var answer = MessageBox.Show(
            this,
            "Ollama is not installed. Download the standalone Ollama CLI from ollama.com? It is about 1.4 GB because it includes GPU libraries. No desktop app will be installed, and models are downloaded separately.",
            "Install Ollama CLI",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (answer != DialogResult.Yes)
        {
            Log("Ollama installation skipped.");
            return false;
        }

        executable = await _ollama.InstallAsync(new Progress<OllamaProgress>(UpdateProgress));
        Log($"Ollama installed: {executable}");
        return true;
    }

    private async Task RefreshOllamaModelsAsync()
    {
        if (_provider.SelectedIndex != 0 || _refreshingModels)
        {
            return;
        }

        _refreshingModels = true;
        try
        {
            var selected = _model.Text.Trim();
            var models = _ollama.GetDownloadedModels();
            IReadOnlyList<string> choices = models;
            if (models.Count == 0)
            {
                Log("No downloaded Ollama models found. Checking this PC...");
                choices = await Task.Run(OllamaDriver.RecommendModelsForThisPc);
            }

            if (_provider.SelectedIndex != 0)
            {
                return;
            }

            _ollamaSelection = models.Count == 0
                ? choices[0]
                : models.FirstOrDefault(model =>
                    model.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? models[0];
            _ollamaModelChoices = choices;
            ApplyModelChoices(_ollamaModelChoices, remote: false);

            Log(models.Count == 0
                ? $"Recommended for this GPU: {string.Join(" or ", choices)}"
                : $"Found {models.Count} downloaded Ollama model{(models.Count == 1 ? string.Empty : "s")}.");
        }
        finally
        {
            _refreshingModels = false;
        }
    }

    private async Task RefreshGpuStatusAsync()
    {
        _gpuStatus.Text = "Detecting...";
        _gpuStatus.ForeColor = Color.FromArgb(70, 76, 86);
        try
        {
            var (gpu, vram) = await Task.Run(() =>
                (OllamaDriver.DetectPreferredGpu(), OllamaDriver.DetectVramGb()));
            _gpuStatus.Text = gpu.Summary;
            _gpuStatus.ForeColor = gpu.Acceleration == GpuAcceleration.None
                ? Color.DarkGoldenrod
                : Color.FromArgb(30, 120, 72);
            _toolTip.SetToolTip(_gpuStatus, gpu.Detail);

            _modelTip.Text = OllamaDriver.RecommendationTip(vram);
            _modelTip.Visible = _provider.SelectedIndex == 0;
        }
        catch (Exception exception)
        {
            _gpuStatus.Text = "Could not detect a GPU";
            _gpuStatus.ForeColor = Color.DarkGoldenrod;
            _toolTip.SetToolTip(_gpuStatus, exception.Message);
        }
    }

    private void UpdateProviderFields()
    {
        var remote = _provider.SelectedIndex == 1;

        if (remote != (_lastProviderIndex == 1))
        {
            if (_lastProviderIndex == 0)
            {
                _ollamaSelection = _model.Text.Trim();
            }
            else
            {
                _remoteSelection = _model.Text.Trim();
            }
        }

        _lastProviderIndex = remote ? 1 : 0;

        _endpoint.Visible = remote;
        _apiKey.Visible = remote;
        if (_endpointLabel is not null)
        {
            _endpointLabel.Visible = remote;
        }

        if (_apiKeyLabel is not null)
        {
            _apiKeyLabel.Visible = remote;
        }

        _browseCatalog.Visible = !remote;
        _checkModels.Visible = remote;
        _modelTip.Visible = !remote && _modelTip.Text.Length != 0;

        ApplyModelChoices(remote ? _remoteModelChoices : _ollamaModelChoices, remote);
        _configureLlm.Text = remote ? "Start Secure Bridge" : "Configure and Start";
    }

    private void ApplyModelChoices(IReadOnlyList<string> choices, bool remote)
    {
        var preferred = remote ? _remoteSelection : _ollamaSelection;
        _model.BeginUpdate();
        try
        {
            _model.Items.Clear();
            if (choices.Count != 0)
            {
                _model.Items.AddRange(choices.Cast<object>().ToArray());
            }
        }
        finally
        {
            _model.EndUpdate();
        }

        var match = choices.FirstOrDefault(choice =>
            choice.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _model.Text = match;
        }
        else if (choices.Count != 0)
        {
            _model.Text = choices[0];
        }
        else if (remote)
        {
            _model.Text = string.Empty;
            _model.SelectedIndex = -1;
        }
        else if (!string.IsNullOrWhiteSpace(preferred))
        {
            _model.Text = preferred;
        }

        _model.Enabled = !remote || choices.Count != 0;
    }

    private async void CheckRemoteModels(object? sender, EventArgs eventArgs)
    {
        var endpoint = _endpoint.Text.Trim();
        if (endpoint.Length == 0)
        {
            ShowError("Enter the endpoint first.");
            return;
        }

        _checkModels.Enabled = false;
        SetBusy(true);
        try
        {
            Log($"Checking models at {endpoint}...");
            var models = await RemoteModelDiscovery.ListAsync(endpoint, _apiKey.Text);
            if (models.Count == 0)
            {
                Log("The endpoint returned no models.");
                MessageBox.Show(
                    this,
                    "The endpoint is reachable but returned no models for this key.",
                    "Project Remnants Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var previous = _model.Text.Trim();
            _remoteSelection = models.FirstOrDefault(model =>
                model.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? models[0];
            _remoteModelChoices = models;
            ApplyModelChoices(_remoteModelChoices, remote: true);
            Log($"Found {models.Count} model{(models.Count == 1 ? string.Empty : "s")} for this endpoint.");
        }
        catch (Exception exception)
        {
            Log($"Error: {exception.Message}");
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
            _checkModels.Enabled = true;
        }
    }

    private async void ConfigureLlm(object? sender, EventArgs eventArgs)
    {
        SetBusy(true);
        try
        {
            if (_provider.SelectedIndex == 0)
            {
                await ConfigureOllamaAsync();
            }
            else
            {
                await ConfigureRemoteApiAsync();
            }
        }
        catch (Exception exception)
        {
            Log($"Error: {exception.Message}");
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConfigureOllamaAsync()
    {
        await _bridge.StopAsync();
        if (!await EnsureOllamaInstalledAsync())
        {
            return;
        }

        UpdateProgress(new OllamaProgress("Starting Ollama"));
        await _ollama.EnsureServerAsync();
        var model = _model.Text.Trim();
        var models = await _ollama.GetModelsAsync();
        if (!models.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            var answer = MessageBox.Show(
                this,
                $"{model} is not downloaded. Download it now?",
                "Project Remnants Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            await _ollama.PullModelAsync(model, new Progress<OllamaProgress>(UpdateProgress));
            await RefreshOllamaModelsAsync();
        }

        LlmConfigurationStore.SaveOllama(_userDirectory.Text, model);
        Log($"Local model configured: {model}");
        ShowTrayIfNeeded();
    }

    private async Task ConfigureRemoteApiAsync()
    {
        var profile = new RemoteApiProfile(
            _endpoint.Text.Trim(),
            _model.Text.Trim(),
            _apiKey.Text);
        await _bridge.StopAsync();
        await _bridge.StartAsync(profile.Endpoint, profile.ApiKey);
        LlmConfigurationStore.SaveRemoteProfile(_userDirectory.Text, profile);
        LlmConfigurationStore.SaveOpenAiBridge(
            _userDirectory.Text,
            profile.Model,
            _bridge.Endpoint,
            _bridge.LocalToken);
        Log($"Secure API bridge started at {_bridge.Endpoint}");
        ShowTrayIfNeeded();
    }

    private async void DisableLlm(object? sender, EventArgs eventArgs)
    {
        SetBusy(true);
        try
        {
            await _bridge.StopAsync();
            await _ollama.UnloadModelAsync(_model.Text);
            LlmConfigurationStore.Disable(_userDirectory.Text);
            Log("LLM integration disabled.");
            ShowTrayIfNeeded();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadSavedLlmSettings()
    {
        var configuration = LlmConfigurationStore.LoadProvider(_userDirectory.Text);
        if (configuration is null)
        {
            return;
        }

        _model.Text = configuration.Model;
        if (!configuration.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            _ollamaSelection = configuration.Model;
            _provider.SelectedIndex = 0;
            return;
        }

        var profile = LlmConfigurationStore.LoadRemoteProfile(_userDirectory.Text);
        if (profile is null)
        {
            return;
        }

        _endpoint.Text = profile.Endpoint;
        _model.Text = profile.Model;
        _apiKey.Text = profile.ApiKey;
        _remoteSelection = profile.Model;
        _provider.SelectedIndex = 1;
    }

    private void SetBusy(bool busy)
    {
        _configureLlm.Enabled = !busy;
        _disableLlm.Enabled = !busy;
        _progressPanel.Visible = busy;
        if (busy)
        {
            UpdateProgress(new OllamaProgress("Working"));
        }
    }

    private void UpdateProgress(OllamaProgress update)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateProgress(update));
            return;
        }

        if (update.Percent is int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = percent;
            _progressStatus.Text = $"{update.Message} — {percent}%";
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progressStatus.Text = update.Message;
        }

        if (update.Log)
        {
            Log(update.Message);
        }
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }

        _activity.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayIcon.Icon = Icon;
        _trayIcon.Text = "Project Remnants Setup";
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void MinimizeToTray()
    {
        if (WindowState != FormWindowState.Minimized ||
            !_bridge.IsRunning && !_ollama.OwnsServer)
        {
            return;
        }

        Hide();
        _trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _trayIcon.Visible = false;
    }

    private void ShowTrayIfNeeded()
    {
        _trayIcon.Visible = _bridge.IsRunning || _ollama.OwnsServer;
    }

    private void HandleClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose || !_bridge.IsRunning && !_ollama.OwnsServer)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
        _trayIcon.Visible = true;
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _ollama.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Icon?.Dispose();
        base.OnFormClosed(eventArgs);
    }

    private static Icon LoadAppIcon()
    {
        const string icon = "AAABAAEAICAAAAEAIAC3AgAAFgAAAIlQTkcNChoKAAAADUlIRFIAAAAgAAAAIAgGAAAAc3p69AAAAAFzUkdCAK7OHOkAAAJxSURBVFiF7ZQ9TJNRFIaf27gaE0SQDVfohxBLfym3ISEQTEiIGzMancRCUWNCGmMUAcMoCYuLBoaGRPCnEoWvQGuRqAkyuhkISDrgCt9xqK3C0H51cLHPeO+be99zznsvlClT5n9HFRNop1tQWZm5kbanB1DKlt5RTCAiDEZvIJZ1ZL21vlm04RFteOS4PtTpR3Fk+e/QhkeGw9dlLjYtc7Fp6dZdAtkq52LTMj87I/OzM5KrOmS4JRIZkK2dbdna2ZaQ4S7qonAHREBBejVJky/IfmYP7XRLqNNPky9Io6eFJm8LA9F+tOEREegPhwGYHB2xVeSJQpunTldixlMArLy9BIDu8OHyeXOxyKPbvZhvstrJhyMsxZO2DBQNSY5u3SX7mT10hx9z4T1PX8cA+JhcZjw6kTegFAj2Agg2QpjjuflSiYDL58U6OKS3vQeAseFHiGXRHPABsLSxpuxeDiV0ALLJV0oxEO3nQkDzKZVg/dptLAWJ6rPIoYX5Za2kM0sS/2ni2cIsAFONARbPVCEiJDY/lHye7RFop1u04ZG2ve+ICJ/XVult78H1+AF9DbV5cxeDHSV9ALYN5D4kgLauAKN3xhERxoYnOLAOuXk/wtC9QX5kMqXcX/gZQrZypSC0u4tSAgiLr5KISN7Yk81vBM0VvMEAoU4/IiJKKVsjKWhAGx6J3A2zfmUIz9Qo6ctDvKuqgmPz1k63JOIpHEpxdegWMIJSCqWUFAtlwc1c4Poaavm6kGaxurpgylvrm6Wypgbn+XOY8ZStYBZtUbDOJQ7H76gUMhCsc4n69UWerKjgxXK85FdRpkyZMv+cnwO8DtoanKMRAAAAAElFTkSuQmCC";
        using var stream = new MemoryStream(Convert.FromBase64String(icon));
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    private static void ShowError(string message) => MessageBox.Show(
        message,
        "Project Remnants Setup",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
}
