namespace ProjectRemnants.Setup.Voice;

public sealed class VoicePage : TabPage
{
    private readonly VoiceInstaller _installer;
    private readonly ComboBox _model = new();
    private readonly Label _summary = new();
    private readonly Label _status = new();
    private readonly Label _progressText = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _install = new();
    private readonly Button _remove = new();
    private CancellationTokenSource? _running;

    public VoicePage(VoiceInstaller? installer = null) : base("Voice Input")
    {
        _installer = installer ?? new VoiceInstaller();
        BackColor = Color.FromArgb(245, 246, 248);
        Padding = new Padding(16);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 0, 8)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var heading = new Label
        {
            Text = "Speak to survivors (optional)",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11F),
            Margin = new Padding(0, 0, 0, 6)
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);

        var intro = new Label
        {
            Text = "Voice input turns what you say into text for conversations. It runs entirely on this PC; "
                + "nothing is uploaded. In the game, open a conversation, switch Voice on, then hold the "
                + "push-to-talk key (Right Ctrl by default) while you speak.",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = Color.FromArgb(70, 76, 86),
            Margin = new Padding(0, 0, 0, 10)
        };
        layout.Controls.Add(intro, 0, 1);
        layout.SetColumnSpan(intro, 2);

        layout.Controls.Add(
            new Label { Text = "Model", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) },
            0, 2);
        _model.DropDownStyle = ComboBoxStyle.DropDownList;
        _model.Width = 280;
        foreach (var model in VoiceCatalog.Models)
        {
            _model.Items.Add(model);
        }

        _model.Format += (_, args) => args.Value = ((VoiceModel)args.ListItem!).Title;
        _model.SelectedIndex = 0;
        _model.SelectedIndexChanged += (_, _) => RefreshStatus();
        layout.Controls.Add(_model, 1, 2);

        _summary.AutoSize = true;
        _summary.MaximumSize = new Size(620, 0);
        _summary.ForeColor = Color.FromArgb(70, 76, 86);
        _summary.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(_summary, 1, 3);

        _status.AutoSize = true;
        _status.MaximumSize = new Size(620, 0);
        _status.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(_status, 1, 4);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 4, 0, 4)
        };
        ConfigureButton(_install, "Install / Update", InstallClicked);
        ConfigureButton(_remove, "Remove voice input", RemoveClicked);
        actions.Controls.Add(_install);
        actions.Controls.Add(_remove);
        layout.Controls.Add(actions, 1, 5);

        _progress.Width = 420;
        _progress.Visible = false;
        _progress.Margin = new Padding(0, 6, 0, 2);
        layout.Controls.Add(_progress, 1, 6);
        _progressText.AutoSize = true;
        _progressText.ForeColor = Color.FromArgb(70, 76, 86);
        _progressText.Visible = false;
        layout.Controls.Add(_progressText, 1, 7);

        Controls.Add(layout);
        RefreshStatus();
    }

    private VoiceModel SelectedModel => (VoiceModel)_model.SelectedItem!;

    private static void ConfigureButton(Button button, string text, EventHandler handler)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Padding = new Padding(10, 3, 10, 3);
        button.Margin = new Padding(0, 0, 8, 0);
        button.Click += handler;
    }

    private void RefreshStatus()
    {
        var status = _installer.GetStatus();
        var model = SelectedModel;
        _summary.Text = model.Summary;
        var installed = status.InstalledModelFiles.Contains(
            model.Asset.FileName, StringComparer.OrdinalIgnoreCase);
        if (status.EngineInstalled && installed)
        {
            var active = string.Equals(status.ActiveModelFile, model.Asset.FileName, StringComparison.OrdinalIgnoreCase);
            _status.ForeColor = Color.FromArgb(30, 120, 72);
            _status.Text = active
                ? "Installed and in use."
                : "Installed. Press Install / Update to use this model.";
        }
        else if (status.EngineInstalled)
        {
            _status.ForeColor = Color.FromArgb(70, 76, 86);
            _status.Text = "The speech engine is installed. This model is not downloaded yet.";
        }
        else
        {
            _status.ForeColor = Color.FromArgb(70, 76, 86);
            _status.Text = "Not installed.";
        }

        _remove.Enabled = _running is null && (status.EngineInstalled || status.InstalledModelFiles.Count > 0);
    }

    private async void InstallClicked(object? sender, EventArgs args)
    {
        if (_running is not null)
        {
            return;
        }

        _running = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<VoiceProgress>(report =>
        {
            _progressText.Text = report.Message;
            if (report.Percent is { } percent)
            {
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = Math.Clamp(percent, 0, 100);
            }
            else
            {
                _progress.Style = ProgressBarStyle.Marquee;
            }
        });
        try
        {
            await _installer.InstallAsync(SelectedModel, progress, _running.Token);
            _progressText.Text = "Voice input is ready.";
        }
        catch (OperationCanceledException)
        {
            _progressText.Text = "Cancelled.";
        }
        catch (Exception failure) when (failure is IOException or HttpRequestException
            or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _progressText.Text = "Could not finish: " + failure.Message;
        }
        finally
        {
            _running.Dispose();
            _running = null;
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void RemoveClicked(object? sender, EventArgs args)
    {
        if (_running is not null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Remove the speech engine and all downloaded voice models?",
            "Remove voice input",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            _installer.Remove();
            _progressText.Text = "Voice input was removed.";
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            _progressText.Text = "Could not remove it: " + failure.Message;
        }

        _progressText.Visible = true;
        RefreshStatus();
    }

    private void SetBusy(bool busy)
    {
        _install.Enabled = !busy;
        _remove.Enabled = !busy;
        _model.Enabled = !busy;
        _progress.Visible = busy;
        _progressText.Visible = true;
        if (busy)
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Value = 0;
        }
    }
}
