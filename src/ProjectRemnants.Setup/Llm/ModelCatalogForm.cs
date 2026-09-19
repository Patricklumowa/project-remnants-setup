using System.Globalization;

namespace ProjectRemnants.Setup.Llm;

public sealed class ModelCatalogForm : Form
{
    private static readonly Color PageBackground = Color.FromArgb(245, 246, 248);
    private static readonly Color Accent = Color.FromArgb(52, 105, 204);
    private static readonly Color CardBackground = Color.White;
    private static readonly Color TextPrimary = Color.FromArgb(34, 39, 48);
    private static readonly Color TextMuted = Color.FromArgb(112, 119, 130);
    private static readonly Color Good = Color.FromArgb(30, 120, 72);
    private static readonly Color Warning = Color.FromArgb(176, 118, 16);
    private static readonly Color Bad = Color.FromArgb(196, 74, 62);

    private readonly OllamaDriver _ollama;
    private readonly double _vramGb;
    private readonly double _ramGb;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly TextBox _search = new();
    private readonly ComboBox _filter = new();
    private readonly ModelListBox _list = new();
    private readonly Label _empty = new();
    private readonly Label _detailName = new();
    private readonly Label _detailMeta = new();
    private readonly Label _detailDescription = new();
    private readonly Label _detailVerdict = new();
    private readonly Label _sizeHeading = new();
    private readonly FlowLayoutPanel _sizeChips = new();
    private readonly Button _download = new();
    private readonly Button _use = new();
    private readonly Button _refresh = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private IReadOnlyList<CatalogModel> _models = [];
    private IReadOnlyList<CatalogRow> _rows = [];
    private HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _chips = new(StringComparer.OrdinalIgnoreCase);
    private CatalogModel? _selectedModel;
    private string? _selectedTag;
    private bool _busy;

    public string? SelectedModel { get; private set; }

    public ModelCatalogForm(OllamaDriver ollama, double vramGb, double ramGb)
    {
        _ollama = ollama;
        _vramGb = vramGb;
        _ramGb = ramGb;

        Text = "Model catalogue";
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoScaleDimensions = new SizeF(6F, 13F);
        ClientSize = new Size(940, 600);
        MinimumSize = new Size(680, 440);
        BackColor = PageBackground;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var content = BuildContent();
        Controls.Add(content);
        Controls.Add(BuildHeader());
        Controls.Add(BuildFooter());
        Shown += (_, _) =>
        {
            if (content is SplitContainer split)
            {
                split.Panel1MinSize = 280;
                split.Panel2MinSize = 340;
                split.SplitterDistance = Math.Max(
                    split.Panel1MinSize,
                    Math.Min(380, split.Width - split.Panel2MinSize - split.SplitterWidth));
            }

            _ = LoadCatalogAsync();
        };
        FormClosed += (_, _) => _http.Dispose();
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = PageBackground,
            Padding = new Padding(14, 12, 14, 8)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _search.PlaceholderText = "Search models...";
        _search.Font = new Font("Segoe UI", 10F);
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.Dock = DockStyle.Fill;
        _search.Margin = new Padding(0, 0, 8, 0);
        _search.TextChanged += (_, _) => PopulateList();

        _filter.DropDownStyle = ComboBoxStyle.DropDownList;
        _filter.FlatStyle = FlatStyle.Flat;
        _filter.Items.AddRange(["All sizes", "Fits my GPU", "Runs in RAM", "Too big"]);
        _filter.SelectedIndex = 0;
        _filter.Width = 150;
        _filter.Margin = new Padding(0, 1, 8, 0);
        _filter.Anchor = AnchorStyles.Left;
        _filter.SelectedIndexChanged += (_, _) => PopulateList();

        _refresh.Text = "Refresh";
        _refresh.FlatStyle = FlatStyle.Flat;
        _refresh.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 213);
        _refresh.BackColor = CardBackground;
        _refresh.AutoSize = true;
        _refresh.Margin = new Padding(0, 0, 8, 0);
        _refresh.Anchor = AnchorStyles.Left;
        _refresh.Click += async (_, _) => await LoadCatalogAsync();

        var legend = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = PageBackground,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 0, 0)
        };
        legend.Controls.Add(CreateLegendItem(Good, "fits GPU"));
        legend.Controls.Add(CreateLegendItem(Warning, "RAM only"));
        legend.Controls.Add(CreateLegendItem(Bad, "too big"));

        header.Controls.Add(_search, 0, 0);
        header.Controls.Add(_filter, 1, 0);
        header.Controls.Add(_refresh, 2, 0);
        header.Controls.Add(legend, 3, 0);
        return header;
    }

    private Control BuildContent()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 1,
            BackColor = Color.FromArgb(226, 230, 236)
        };

        split.Panel1.BackColor = CardBackground;

        _list.Dock = DockStyle.Fill;
        _list.SelectedIndexChanged += (_, _) => OnModelSelected();

        _empty.Dock = DockStyle.Fill;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.ForeColor = TextMuted;
        _empty.BackColor = CardBackground;
        _empty.Text = "Loading catalogue...";
        _empty.Visible = false;

        split.Panel1.Controls.Add(_empty);
        split.Panel1.Controls.Add(_list);
        _empty.BringToFront();

        split.Panel2.BackColor = CardBackground;
        split.Panel2.Padding = new Padding(20, 16, 20, 12);
        split.Panel2.Controls.Add(BuildDetail());
        return split;
    }

    private Control BuildDetail()
    {
        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = CardBackground
        };
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _detailName.AutoSize = true;
        _detailName.Font = new Font("Segoe UI Semibold", 15F);
        _detailName.ForeColor = TextPrimary;
        _detailName.Margin = new Padding(0, 0, 0, 2);
        _detailName.Text = "Select a model";

        _detailMeta.AutoSize = true;
        _detailMeta.ForeColor = TextMuted;
        _detailMeta.Font = new Font("Segoe UI", 9F);
        _detailMeta.Margin = new Padding(0, 0, 0, 10);

        _detailDescription.AutoSize = true;
        _detailDescription.MaximumSize = new Size(520, 0);
        _detailDescription.ForeColor = TextPrimary;
        _detailDescription.Font = new Font("Segoe UI", 9.5F);
        _detailDescription.Margin = new Padding(0, 0, 0, 14);

        _detailVerdict.AutoSize = true;
        _detailVerdict.MaximumSize = new Size(520, 0);
        _detailVerdict.Font = new Font("Segoe UI Semibold", 10.5F);
        _detailVerdict.ForeColor = Bad;
        _detailVerdict.Visible = false;
        _detailVerdict.Margin = new Padding(0, 0, 0, 12);

        _sizeHeading.AutoSize = true;
        _sizeHeading.Text = "Choose a size";
        _sizeHeading.Font = new Font("Segoe UI Semibold", 9.5F);
        _sizeHeading.ForeColor = TextPrimary;
        _sizeHeading.Margin = new Padding(0, 0, 0, 6);

        _sizeChips.Dock = DockStyle.Fill;
        _sizeChips.AutoScroll = true;
        _sizeChips.FlowDirection = FlowDirection.LeftToRight;
        _sizeChips.WrapContents = true;
        _sizeChips.BackColor = CardBackground;

        detail.Controls.Add(_detailName, 0, 0);
        detail.Controls.Add(_detailMeta, 0, 1);
        detail.Controls.Add(_detailDescription, 0, 2);
        detail.Controls.Add(_detailVerdict, 0, 3);
        detail.Controls.Add(_sizeHeading, 0, 4);
        detail.Controls.Add(_sizeChips, 0, 5);
        return detail;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = PageBackground,
            Padding = new Padding(14, 8, 14, 10)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Dock = DockStyle.Fill;
        _progress.Margin = new Padding(0, 4, 10, 4);
        _progress.Visible = false;

        _status.AutoEllipsis = true;
        _status.ForeColor = TextMuted;
        _status.BackColor = PageBackground;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Dock = DockStyle.Fill;
        _status.Margin = new Padding(0, 0, 8, 0);

        StyleButton(_download, "Download", primary: true, accent: Good);
        StyleButton(_use, "Use this model", primary: false);
        _download.AutoSize = true;
        _use.AutoSize = true;
        _download.Margin = new Padding(0, 0, 8, 0);
        _use.Margin = new Padding(0);
        _download.Anchor = AnchorStyles.Right;
        _use.Anchor = AnchorStyles.Right;
        _download.Click += async (_, _) => await DownloadSelectedAsync();
        _use.Click += (_, _) => UseSelected();

        footer.Controls.Add(_progress, 0, 0);
        footer.Controls.Add(_status, 1, 0);
        footer.Controls.Add(_download, 2, 0);
        footer.Controls.Add(_use, 3, 0);
        return footer;
    }

    private static void StyleButton(Button button, string text, bool primary, Color? accent = null)
    {
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.Padding = new Padding(12, 4, 12, 4);
        button.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        if (primary)
        {
            var color = accent ?? Accent;
            button.BackColor = color;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = color;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.15f);
        }
        else
        {
            button.BackColor = CardBackground;
            button.ForeColor = TextPrimary;
            button.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 213);
        }
    }

    private async Task LoadCatalogAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Loading the model catalogue from ollama.com...");
        _empty.Text = "Loading catalogue...";
        _empty.Visible = _models.Count == 0;
        try
        {
            _models = await OllamaCatalog.LoadAsync(_http);
            _installed = new HashSet<string>(
                _ollama.GetDownloadedModels().Select(Normalize), StringComparer.OrdinalIgnoreCase);
            PopulateList();
            if (_models.Count != 0)
            {
                _status.Text = $"{_models.Count} models available. Green fits your " +
                    $"{_vramGb:0.#} GB GPU.";
            }
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void PopulateList()
    {
        var query = _search.Text.Trim();
        var filter = _filter.SelectedIndex;
        _rows = _models
            .Where(model => MatchesSearch(model, query))
            .Select(model => BuildRow(model))
            .Where(row => MatchesFilter(row.Best, filter))
            .ToArray();

        var previous = _selectedModel;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _rows)
        {
            _list.Items.Add(row);
        }

        _list.EndUpdate();

        _empty.Visible = _rows.Count == 0;
        _empty.Text = _models.Count == 0
            ? "Could not load the catalogue. Check your connection."
            : "No models match your search.";

        var restored = previous is null
            ? -1
            : _rows.ToList().FindIndex(row =>
                row.Model.Name.Equals(previous.Name, StringComparison.OrdinalIgnoreCase));
        if (restored >= 0)
        {
            _list.SelectedIndex = restored;
        }
        else if (_rows.Count != 0)
        {
            _list.SelectedIndex = 0;
        }
        else
        {
            ShowDetail(null);
        }
    }

    private CatalogRow BuildRow(CatalogModel model)
    {
        var best = Runability.Unknown;
        foreach (var tag in model.Tags)
        {
            best = Better(best, Evaluate(tag.ParametersB));
        }

        if (model.Tags.Count == 0)
        {
            best = Evaluate(0);
        }

        return new CatalogRow(model, best);
    }

    private static Runability Better(Runability left, Runability right) =>
        Rank(right) > Rank(left) ? right : left;

    private static int Rank(Runability value) => value switch
    {
        Runability.Gpu => 3,
        Runability.CpuOnly => 2,
        Runability.TooBig => 1,
        _ => 0
    };

    private static bool MatchesSearch(CatalogModel model, string query) =>
        query.Length == 0 ||
        model.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        model.Description.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesFilter(Runability runability, int filter) => filter switch
    {
        1 => runability == Runability.Gpu,
        2 => runability == Runability.CpuOnly,
        3 => runability == Runability.TooBig,
        _ => true
    };

    private Runability Evaluate(double parametersB)
    {
        var download = ModelFit.EstimateDownloadGb(parametersB);
        return ModelFit.Evaluate(download, _vramGb, _ramGb);
    }

    private void OnModelSelected()
    {
        var model = _list.SelectedItem is CatalogRow row ? row.Model : null;
        ShowDetail(model);
    }

    private void ShowDetail(CatalogModel? model)
    {
        _selectedModel = model;
        _sizeChips.Controls.Clear();
        _chips.Clear();
        _selectedTag = null;

        if (model is null)
        {
            _detailName.Text = "Select a model";
            _detailMeta.Text = string.Empty;
            _detailDescription.Text = "Pick a model on the left to see its sizes and how they fit your PC.";
            _detailVerdict.Visible = false;
            _sizeHeading.Visible = false;
            _sizeChips.Visible = false;
            UpdateButtons();
            return;
        }

        _detailName.Text = model.Name;
        var capabilities = model.Capabilities.Count == 0
            ? "chat"
            : string.Join("  \u00B7  ", model.Capabilities);
        var sizes = model.Tags.Count == 0
            ? "no size tags"
            : $"{model.Tags.Count} size{(model.Tags.Count == 1 ? string.Empty : "s")}";
        _detailMeta.Text = $"{capabilities}    |    {sizes}";
        _detailDescription.Text = model.Description;
        _detailVerdict.Visible = false;
        _sizeHeading.Visible = true;
        _sizeChips.Visible = true;

        foreach (var name in model.DownloadNames)
        {
            var parameters = ParametersFor(model, name);
            var runability = Evaluate(parameters);
            var chip = CreateChip(model, name, parameters, runability);
            _chips[name] = chip;
            _sizeChips.Controls.Add(chip);
        }

        var preferred = model.DownloadNames
            .OrderByDescending(name => Rank(Evaluate(ParametersFor(model, name))))
            .ThenBy(name => ParametersFor(model, name))
            .FirstOrDefault();
        if (preferred is not null)
        {
            SelectTag(preferred);
        }
        else
        {
            UpdateButtons();
        }
    }

    private static double ParametersFor(CatalogModel model, string name) =>
        model.Tags.Count == 0
            ? 0
            : model.Tags.First(tag =>
                name.EndsWith($":{tag.Tag}", StringComparison.OrdinalIgnoreCase)).ParametersB;

    private static Control CreateLegendItem(Color color, string text)
    {
        var item = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = PageBackground,
            Margin = new Padding(0, 0, 14, 0)
        };
        item.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "\u25CF",
            ForeColor = color,
            BackColor = PageBackground,
            Margin = new Padding(0, 0, 4, 0)
        });
        item.Controls.Add(new Label
        {
            AutoSize = true,
            Text = text,
            ForeColor = TextMuted,
            BackColor = PageBackground,
            Margin = new Padding(0, 0, 0, 0)
        });
        return item;
    }

    private Button CreateChip(CatalogModel model, string name, double parametersB, Runability runability)
    {
        var tag = name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : model.Name;
        var size = parametersB <= 0
            ? string.Empty
            : $"   {FormatParameters(parametersB)} \u00B7 {OllamaCatalog.FormatSize(ModelFit.EstimateDownloadGb(parametersB))}";
        var chip = new Button
        {
            Text = $"{tag}{size}",
            AutoSize = true,
            Padding = new Padding(14, 8, 14, 8),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F),
            Margin = new Padding(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Tag = name,
            BackColor = CardBackground,
            ForeColor = StatusColor(runability)
        };
        chip.FlatAppearance.BorderColor = Color.FromArgb(210, 215, 222);
        var installed = _installed.Contains(Normalize(name));
        chip.FlatAppearance.BorderColor = installed
            ? Color.FromArgb(150, 190, 160)
            : Color.FromArgb(210, 215, 222);
        chip.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 244, 250);
        chip.Click += (_, _) => SelectTag(name);
        return chip;
    }

    private void SelectTag(string name)
    {
        _selectedTag = name;
        foreach (var (tag, chip) in _chips)
        {
            var selected = tag.Equals(name, StringComparison.OrdinalIgnoreCase);
            chip.FlatAppearance.BorderSize = selected ? 2 : 1;
            chip.FlatAppearance.BorderColor = selected
                ? Accent
                : Color.FromArgb(210, 215, 222);
            chip.BackColor = selected ? Color.FromArgb(234, 241, 253) : CardBackground;
        }

        UpdateVerdict(name);
        UpdateButtons();
    }

    private void UpdateVerdict(string? tag)
    {
        var runability = Runability.Unknown;
        if (tag is not null && _selectedModel is { } model)
        {
            runability = Evaluate(ParametersFor(model, tag));
        }

        if (runability == Runability.TooBig)
        {
            _detailVerdict.Text = TooBigVerdict;
            _detailVerdict.Visible = true;
        }
        else
        {
            _detailVerdict.Visible = false;
        }
    }

    private static string FormatParameters(double billions) => billions <= 0
        ? "?"
        : billions.ToString(billions >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture) + "B";

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static Color StatusColor(Runability runability) => runability switch
    {
        Runability.Gpu => Good,
        Runability.CpuOnly => Warning,
        Runability.TooBig => Bad,
        _ => TextMuted
    };

    private static string ShortStatus(Runability runability) => runability switch
    {
        Runability.Gpu => "Fits GPU",
        Runability.CpuOnly => "RAM only",
        Runability.TooBig => "Too big",
        _ => "Unknown"
    };

    private const string TooBigVerdict = "yeah ur gpu aint running ts twin";

    private bool IsInstalled(string? name) =>
        name is not null && _installed.Contains(Normalize(name));

    private void UpdateButtons()
    {
        var hasTag = _selectedTag is not null;
        var installed = IsInstalled(_selectedTag);
        _download.Enabled = hasTag && !_busy && _ollama.FindExecutable() is not null;
        _use.Enabled = hasTag && installed && !_busy;
        _use.Text = hasTag && !installed ? "Download first" : "Use this model";
    }

    private void UseSelected()
    {
        if (_selectedTag is null)
        {
            return;
        }

        if (!IsInstalled(_selectedTag))
        {
            MessageBox.Show(
                this,
                $"{_selectedTag} is not downloaded yet. Download it first, then choose it.",
                "Project Remnants Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SelectedModel = _selectedTag;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task DownloadSelectedAsync()
    {
        var tag = _selectedTag;
        if (tag is null || _busy)
        {
            return;
        }

        if (_ollama.FindExecutable() is null)
        {
            MessageBox.Show(
                this,
                "Ollama is not installed yet. Configure it on the LLM Companion tab first.",
                "Project Remnants Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SetBusy(true, $"Downloading {tag}...");
        try
        {
            await _ollama.PullModelAsync(tag, new Progress<OllamaProgress>(Report));
            _installed.Add(Normalize(tag));
            var model = _selectedModel;
            PopulateList();
            if (model is not null)
            {
                ShowDetail(model);
                if (_chips.ContainsKey(tag))
                {
                    SelectTag(tag);
                }
            }

            _status.Text = $"{tag} downloaded. Click \"Use this model\" to select it.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Project Remnants Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void Report(OllamaProgress update)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Report(update));
            return;
        }

        if (update.Percent is int percent)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = Math.Clamp(percent, 0, 100);
            _status.Text = $"{update.Message} - {percent}%";
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _status.Text = update.Message;
        }
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        _search.Enabled = !busy;
        _filter.Enabled = !busy;
        _refresh.Enabled = !busy;
        _sizeChips.Enabled = !busy;
        if (message is not null)
        {
            _status.Text = message;
        }

        _progress.Visible = busy;
        _progress.Style = ProgressBarStyle.Marquee;

        UpdateButtons();
    }

    private sealed record CatalogRow(CatalogModel Model, Runability Best);

    private sealed class ModelListBox : ListBox
    {
        private readonly Font _nameFont = new("Segoe UI Semibold", 10F);
        private readonly Font _metaFont = new("Segoe UI", 8.5F);
        private readonly Font _badgeFont = new("Segoe UI Semibold", 8.5F);
        private readonly int _nameHeight;
        private readonly int _metaHeight;

        public ModelListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            BackColor = CardBackground;

            _nameHeight = TextRenderer.MeasureText("Ag", _nameFont).Height;
            _metaHeight = TextRenderer.MeasureText("Ag", _metaFont).Height;
            ItemHeight = _nameHeight + _metaHeight + Scale(19);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (DrawMode == DrawMode.OwnerDrawFixed)
            {
                ItemHeight = _nameHeight + _metaHeight + Scale(19);
            }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not CatalogRow row)
            {
                return;
            }

            var bounds = e.Bounds;
            var padding = Scale(10);
            var stripeWidth = Scale(4);
            var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var background = new SolidBrush(
                selected ? Color.FromArgb(234, 241, 253) : CardBackground))
            {
                e.Graphics.FillRectangle(background, bounds);
            }

            var stripe = StatusColor(row.Best);
            using (var stripeBrush = new SolidBrush(selected || row.Best != Runability.Unknown
                ? stripe
                : Color.FromArgb(224, 228, 234)))
            {
                e.Graphics.FillRectangle(
                    stripeBrush,
                    bounds.Left,
                    bounds.Top + padding,
                    stripeWidth,
                    bounds.Height - (2 * padding));
            }

            var badge = ShortStatus(row.Best);
            var badgeSize = TextRenderer.MeasureText(badge, _badgeFont);
            var textLeft = bounds.Left + stripeWidth + padding;
            var textWidth = bounds.Width - textLeft - badgeSize.Width - (2 * padding);

            var nameTop = bounds.Top + padding - Scale(2);
            TextRenderer.DrawText(
                e.Graphics,
                row.Model.Name,
                _nameFont,
                new Rectangle(textLeft, nameTop, textWidth, _nameHeight),
                TextPrimary,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(
                e.Graphics,
                Meta(row),
                _metaFont,
                new Rectangle(textLeft, nameTop + _nameHeight, textWidth, _metaHeight),
                TextMuted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);

            var badgeTop = bounds.Top + ((bounds.Height - badgeSize.Height) / 2);
            TextRenderer.DrawText(
                e.Graphics,
                badge,
                _badgeFont,
                new Rectangle(bounds.Right - badgeSize.Width - padding, badgeTop, badgeSize.Width, badgeSize.Height),
                stripe,
                TextFormatFlags.NoPrefix);

            using (var separator = new Pen(Color.FromArgb(238, 240, 244)))
            {
                e.Graphics.DrawLine(
                    separator,
                    bounds.Left + padding,
                    bounds.Bottom - 1,
                    bounds.Right - padding,
                    bounds.Bottom - 1);
            }
        }

        private int Scale(int value) => (int)Math.Round(value * (DeviceDpi / 96f));

        private static string Meta(CatalogRow row)
        {
            var model = row.Model;
            if (model.Tags.Count == 0)
            {
                return "no published sizes";
            }

            var smallest = model.Tags[0].ParametersB;
            var largest = model.Tags[^1].ParametersB;
            var range = Math.Abs(largest - smallest) < 0.001
                ? Parameters(smallest)
                : $"{Parameters(smallest)}-{Parameters(largest)}";
            var capabilities = model.Capabilities.Count == 0
                ? string.Empty
                : "  \u00B7  " + string.Join(", ", model.Capabilities);
            return $"{range}  \u00B7  {model.Tags.Count} size{(model.Tags.Count == 1 ? string.Empty : "s")}{capabilities}";
        }

        private static string Parameters(double billions) =>
            billions >= 10
                ? billions.ToString("0", CultureInfo.InvariantCulture) + "B"
                : billions.ToString("0.#", CultureInfo.InvariantCulture) + "B";

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _nameFont.Dispose();
                _metaFont.Dispose();
                _badgeFont.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
