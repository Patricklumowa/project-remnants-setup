using System.Text.Json;

namespace ProjectRemnants.Setup.Benchmark;

public sealed class BenchmarkForm : Form
{
    private readonly string _model;
    private readonly BenchmarkPack _pack = BenchmarkPack.Load();
    private readonly CancellationTokenSource _cancelRun = new();
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _correctness = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _speed = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _metadata = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 223 };
    private readonly DataGridView _cases = new();
    private readonly TextBox _categories = new();
    private readonly TextBox _caseDetails = new();
    private readonly ToolTip _toolTip = new();
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly Button _export = new() { Text = "Export detailed report...", AutoSize = true, Enabled = false };
    private BenchmarkReport? _report;
    private bool _running;

    public BenchmarkForm(string model)
    {
        _model = model;
        Text = "Forced-model benchmark";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1000, 680);
        MinimumSize = new Size(780, 530);
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
            Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 42F, 28F, 28F, 46F, 46F, 40F })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        layout.Controls.Add(new Label
        {
            Text = "Forced-model benchmark · action extraction only. This is not in-game reliability.",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold)
        }, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_correctness, 0, 2);
        layout.Controls.Add(_speed, 0, 3);
        layout.Controls.Add(_metadata, 0, 4);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.Controls.Add(_cancel, 0, 0);
        actions.Controls.Add(_export, 1, 0);
        _progress.Margin = new Padding(12, 5, 3, 5);
        actions.Controls.Add(_progress, 2, 0);
        layout.Controls.Add(actions, 0, 5);
        _cases.Dock = DockStyle.Fill;
        _cases.ReadOnly = true;
        _cases.AllowUserToAddRows = false;
        _cases.AllowUserToDeleteRows = false;
        _cases.RowHeadersVisible = false;
        _cases.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _cases.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _cases.Columns.Add("case", "Case");
        _cases.Columns.Add("expected", "Expected action");
        _cases.Columns.Add("actual", "Actual action");
        _cases.Columns.Add("result", "Result");
        _cases.Columns.Add("time", "Response ms");
        _cases.Columns.Add("reason", "Failure reason");
        var minimumWidths = new[] { 150, 88, 88, 70, 90, 175 };
        var fillWeights = new[] { 24F, 12F, 12F, 9F, 11F, 32F };
        for (var column = 0; column < _cases.Columns.Count; column++)
        {
            _cases.Columns[column].MinimumWidth = minimumWidths[column];
            _cases.Columns[column].FillWeight = fillWeights[column];
        }
        layout.Controls.Add(_cases, 0, 6);
        _categories.Dock = DockStyle.Fill;
        _categories.Multiline = true;
        _categories.ReadOnly = true;
        _categories.ScrollBars = ScrollBars.Vertical;
        _categories.Text = "Scores appear as cases finish.";
        _caseDetails.Dock = DockStyle.Fill;
        _caseDetails.Multiline = true;
        _caseDetails.ReadOnly = true;
        _caseDetails.ScrollBars = ScrollBars.Vertical;
        _caseDetails.Text = "Select a case to compare the complete expected and actual actions.";
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2,
            RowCount = 2, Margin = Padding.Empty };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.Controls.Add(new Label { Text = "Category scores", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        details.Controls.Add(new Label { Text = "Selected case", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) }, 1, 0);
        details.Controls.Add(_categories, 0, 1);
        details.Controls.Add(_caseDetails, 1, 1);
        layout.Controls.Add(details, 0, 7);
        Controls.Add(layout);
        _cancel.Click += (_, _) => { if (_running) _cancelRun.Cancel(); else Close(); };
        _export.Click += Export;
        _cases.SelectionChanged += (_, _) => ShowSelectedCase();
        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, args) =>
        {
            if (!_running) return;
            args.Cancel = true;
            _cancelRun.Cancel();
        };
    }

    private async Task RunAsync()
    {
        _running = true;
        _status.Text = $"Running 0/{_pack.Cases.Count} cases on {_model}...";
        try
        {
            using var runner = new OllamaBenchmark();
            _report = await runner.RunAsync(_pack, _model,
                new Progress<BenchmarkReport>(UpdateReport), _cancelRun.Token);
            UpdateReport(_report);
        }
        catch (Exception exception)
        {
            _status.Text = "Benchmark stopped: " + exception.Message;
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _running = false;
            _cancel.Text = "Close";
            _export.Enabled = _report is not null;
        }
    }

    private void UpdateReport(BenchmarkReport report)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateReport(report)); return; }
        _report = report;
        foreach (var item in report.Cases.Skip(_cases.Rows.Count))
        {
            var actual = item.ActualAction is JsonElement action &&
                action.TryGetProperty("type", out var type) ? type.ToString() : "—";
            _cases.Rows.Add(item.Name, item.ExpectedAction.GetProperty("type").ToString(),
                actual, item.Status, item.ResponseMs?.ToString("0") ?? "—",
                string.Join("; ", item.FailureReasons));
        }
        _progress.Value = Math.Min(_progress.Maximum, report.Cases.Count);
        var summary = report.Summary;
        _status.Text = $"{(report.FinishedUtc is null ? "Running" : report.Cancelled ? "Cancelled" : "Finished")}: " +
            $"{report.Cases.Count}/{report.TotalCases} cases · report saved automatically";
        _toolTip.SetToolTip(_status, $"Partial report: {report.AutoSavePath}");
        _correctness.Text = $"Action extraction: {summary.Passed} passed, {summary.Failed} failed" +
            (summary.CorrectnessPercent is double percentage ? $" ({percentage:0.#}%)" : "");
        _speed.Text = $"Speed: cold load {Format(report.ColdLoadMs, "ms")}; " +
            $"median {Format(summary.MedianResponseMs, "ms")}; p90 {Format(summary.P90ResponseMs, "ms")}" +
            Environment.NewLine + $"Prompt {Format(summary.PromptTokensPerSecond, "tok/s")}; " +
            $"generation {Format(summary.GenerationTokensPerSecond, "tok/s")}";
        _metadata.Text = $"Tag {_model} · quantization {report.Quantization} · context {report.ContextSetting}" +
            Environment.NewLine + $"Pack {report.TestPackVersion} · placement {report.Placement}";
        _toolTip.SetToolTip(_metadata, _metadata.Text);
        _categories.Text = (summary.Categories.Count == 0 ? "Scores appear as cases finish." :
            string.Join(Environment.NewLine, summary.Categories.Select(pair =>
                $"{pair.Key}: {pair.Value.Passed}/{pair.Value.Total}"))) +
            (report.Warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, report.Warnings));
    }

    private static string Format(double? value, string unit) => value is double number
        ? $"{number:0.#} {unit}" : "not reported";

    private void ShowSelectedCase()
    {
        if (_report is null || _cases.CurrentRow is null ||
            _cases.CurrentRow.Index >= _report.Cases.Count) return;
        var item = _report.Cases[_cases.CurrentRow.Index];
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        _caseDetails.Text = $"{item.Name}: {item.Utterance}{Environment.NewLine}" +
            $"Result: {item.Status}{Environment.NewLine}{Environment.NewLine}" +
            $"Expected action:{Environment.NewLine}{JsonSerializer.Serialize(item.ExpectedAction, jsonOptions)}" +
            $"{Environment.NewLine}{Environment.NewLine}Actual action:{Environment.NewLine}" +
            (item.ActualAction is JsonElement actual
                ? JsonSerializer.Serialize(actual, jsonOptions) : "none / malformed") +
            $"{Environment.NewLine}{Environment.NewLine}Failure reason: " +
            (item.FailureReasons.Count == 0 ? "none" : string.Join("; ", item.FailureReasons));
    }

    private void Export(object? sender, EventArgs args)
    {
        if (_report is null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Export forced-model benchmark report",
            Filter = "JSON report (*.json)|*.json",
            FileName = $"forced-model-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _report.Save(dialog.FileName);
            MessageBox.Show(this, $"Report saved to {dialog.FileName}", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancelRun.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
