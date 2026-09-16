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
    private readonly TextBox _model = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _apiKey = new();
    private readonly Button _configureLlm = new();
    private readonly Button _disableLlm = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBox _activity = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly OllamaDriver _ollama = new();
    private readonly RemoteApiBridge _bridge = new();
    private bool _allowClose;

    public MainForm()
    {
        Text = "Project Remnants Setup";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 590);
        Size = new Size(860, 660);
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(245, 246, 248);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 7) };
        tabs.TabPages.Add(BuildSetupPage());
        tabs.TabPages.Add(BuildLlmPage());
        Controls.Add(tabs);

        BuildTrayMenu();
        Shown += (_, _) => DetectInstall();
        Resize += (_, _) => MinimizeToTray();
        FormClosing += HandleClosing;
    }

    private TabPage BuildSetupPage()
    {
        var page = new TabPage("Game Setup") { BackColor = BackColor, Padding = new Padding(24) };
        var layout = CreatePageLayout();
        layout.Controls.Add(CreateHeading(
            "Java agent installation",
            "Adds NPCFW.jar directly to ProjectZomboid64.json. Nothing remains running after setup."),
            0, 0);
        layout.Controls.Add(CreatePathRow(
            "ProjectZomboid64.json", _configPath, BrowseConfig), 0, 1);
        layout.Controls.Add(CreatePathRow("NPCFW.jar", _agentPath, BrowseAgent), 0, 2);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 18, 0, 8)
        };
        actions.Controls.Add(CreateButton("Install / Repair", InstallAgent, true));
        actions.Controls.Add(CreateButton("Remove", RemoveAgent));
        actions.Controls.Add(CreateButton("Detect Again", (_, _) => DetectInstall()));
        layout.Controls.Add(actions, 0, 3);

        _setupStatus.AutoSize = true;
        _setupStatus.ForeColor = Color.FromArgb(70, 76, 86);
        _setupStatus.Margin = new Padding(0, 12, 0, 0);
        layout.Controls.Add(_setupStatus, 0, 4);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildLlmPage()
    {
        var page = new TabPage("LLM Companion") { BackColor = BackColor, Padding = new Padding(24) };
        var layout = CreatePageLayout();
        layout.Controls.Add(CreateHeading(
            "Optional conversation provider",
            "Configure local Ollama or keep a remote API key inside this app's loopback bridge."),
            0, 0);
        layout.Controls.Add(CreatePathRow(
            "Zomboid user directory", _userDirectory, BrowseUserDirectory), 0, 1);

        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        _provider.Items.AddRange(["Local Ollama", "OpenAI-compatible API"]);
        _provider.SelectedIndex = 0;
        _provider.SelectedIndexChanged += (_, _) => UpdateProviderFields();
        layout.Controls.Add(CreateField("Provider", _provider), 0, 2);

        _model.Text = "llama3.2:3b";
        layout.Controls.Add(CreateField("Model", _model), 0, 3);

        _endpoint.Text = "https://api.openai.com/v1";
        var endpointField = CreateField("Endpoint", _endpoint);
        layout.Controls.Add(endpointField, 0, 4);

        _apiKey.UseSystemPasswordChar = true;
        var apiKeyField = CreateField("API key", _apiKey);
        layout.Controls.Add(apiKeyField, 0, 5);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 14, 0, 6)
        };
        ConfigureButton(_configureLlm, "Configure and Start", ConfigureLlm, true);
        ConfigureButton(_disableLlm, "Disable", DisableLlm);
        actions.Controls.Add(_configureLlm);
        actions.Controls.Add(_disableLlm);
        layout.Controls.Add(actions, 0, 6);

        _progress.Dock = DockStyle.Top;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = false;
        _progress.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_progress, 0, 7);

        _activity.Dock = DockStyle.Fill;
        _activity.Multiline = true;
        _activity.ReadOnly = true;
        _activity.ScrollBars = ScrollBars.Vertical;
        _activity.BackColor = Color.White;
        _activity.MinimumSize = new Size(0, 130);
        _activity.Height = 150;
        layout.Controls.Add(_activity, 0, 8);
        page.Controls.Add(layout);

        _userDirectory.Text = LlmConfigurationStore.DefaultUserDirectory;
        LoadSavedRemoteProfile();
        UpdateProviderFields();
        return page;
    }

    private static TableLayoutPanel CreatePageLayout() => new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        ColumnCount = 1,
        RowCount = 9
    };

    private static Control CreateHeading(string title, string description)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, 20)
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 17F),
            Text = title,
            ForeColor = Color.FromArgb(34, 39, 48)
        });
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = description,
            ForeColor = Color.FromArgb(90, 96, 106),
            Margin = new Padding(0, 6, 0, 0)
        });
        return panel;
    }

    private static Control CreatePathRow(string label, TextBox textBox, EventHandler browse)
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Margin = new Padding(0, 6, 0, 8)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var field = CreateField(label, textBox);
        field.Dock = DockStyle.Fill;
        grid.Controls.Add(field, 0, 0);
        var button = CreateButton("Browse", browse);
        button.Margin = new Padding(10, 27, 0, 0);
        grid.Controls.Add(button, 1, 0);
        return grid;
    }

    private static TableLayoutPanel CreateField(string label, Control control)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            Margin = new Padding(0, 5, 0, 7)
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = label,
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = Color.FromArgb(52, 58, 68)
        });
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 5, 0, 0);
        panel.Controls.Add(control);
        return panel;
    }

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
        button.Padding = new Padding(12, 5, 12, 5);
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

        _agentPath.Text = AgentLocator.Find() ?? _agentPath.Text;
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

    private void RemoveAgent(object? sender, EventArgs eventArgs) =>
        RunInstallerMutation(
            () => ProjectZomboidConfig.Remove(_configPath.Text),
            "--remove", _configPath.Text);

    private void RunInstallerMutation(Action mutation, params string[] elevatedArguments)
    {
        try
        {
            mutation();
            RefreshSetupStatus();
        }
        catch (UnauthorizedAccessException)
        {
            if (!Elevation.Run(elevatedArguments))
            {
                ShowError("Administrator access was cancelled or the setup operation failed.");
            }

            RefreshSetupStatus();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
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
            LoadSavedRemoteProfile();
        }
    }

    private void UpdateProviderFields()
    {
        var remote = _provider.SelectedIndex == 1;
        _endpoint.Visible = remote;
        _apiKey.Visible = remote;
        var endpointContainer = _endpoint.Parent;
        var apiKeyContainer = _apiKey.Parent;
        if (endpointContainer is not null)
        {
            endpointContainer.Visible = remote;
        }

        if (apiKeyContainer is not null)
        {
            apiKeyContainer.Visible = remote;
        }

        _configureLlm.Text = remote ? "Start Secure Bridge" : "Configure and Start";
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
        Log("Checking Ollama...");
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

            Log($"Downloading {model}...");
            await _ollama.PullModelAsync(model, new Progress<string>(Log));
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

    private void LoadSavedRemoteProfile()
    {
        var profile = LlmConfigurationStore.LoadRemoteProfile(_userDirectory.Text);
        if (profile is null)
        {
            return;
        }

        _endpoint.Text = profile.Endpoint;
        _model.Text = profile.Model;
        _apiKey.Text = profile.ApiKey;
        _provider.SelectedIndex = 1;
    }

    private void SetBusy(bool busy)
    {
        _configureLlm.Enabled = !busy;
        _disableLlm.Enabled = !busy;
        _progress.Visible = busy;
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
        _trayIcon.Icon = SystemIcons.Application;
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
        base.OnFormClosed(eventArgs);
    }

    private static void ShowError(string message) => MessageBox.Show(
        message,
        "Project Remnants Setup",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
}
