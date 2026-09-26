using System.Text.Json;

namespace ProjectRemnants.Setup.Benchmark;

public sealed class BenchmarkForm : Form
{
    private readonly string _model;
    private readonly BenchmarkPack _pack = BenchmarkPack.Load();
    private readonly CancellationTokenSource _cancelRun = new();
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _correctness = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _speed = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _metadata = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 223 };
    private readonly DataGridView _cases = new();
    private readonly TextBox _categories = new();
    private readonly TextBox _caseDetails = new();
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
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
            Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 50F, 26F, 26F, 26F, 44F, 36F })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        layout.Controls.Add(new Label
        {
            Text = "Forced-model benchmark · action extraction only. This is not in-game reliability.",
            AutoSize = true, Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold)
        }, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_correctness, 0, 2);
        layout.Controls.Add(_speed, 0, 3);
        layout.Controls.Add(_metadata, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.Add(_cancel);
        actions.Controls.Add(_export);
        actions.Controls.Add(_progress);
        _progress.Width = 400;
        layout.Controls.Add(actions, 0, 5);
        _cases.Dock = DockStyle.Fill;
        _cases.ReadOnly = true;
        _cases.AllowUserToAddRows = false;
        _cases.AllowUserToDeleteRows = false;
        _cases.RowHeadersVisible = false;
        _cases.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _cases.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _cases.Columns.Add("case", "Case");
        _cases.Columns.Add("expected", "Expected action");
        _cases.Columns.Add("actual", "Actual action");
        _cases.Columns.Add("result", "Result");
        _cases.Columns.Add("time", "Response ms");
        _cases.Columns.Add("reason", "Failure reason");
        _cases.Columns[0].Width = 220;
        _cases.Columns[1].Width = 105;
        _cases.Columns[2].Width = 105;
        _cases.Columns[3].Width = 85;
        _cases.Columns[4].Width = 105;
        _cases.Columns[5].Width = 340;
        layout.Controls.Add(_cases, 0, 6);
        _categories.Dock = DockStyle.Fill;
        _categories.Multiline = true;
        _categories.ReadOnly = true;
        _categories.ScrollBars = ScrollBars.Vertical;
        _categories.Text = "Category scores appear as cases finish.";
        _caseDetails.Dock = DockStyle.Fill;
        _caseDetails.Multiline = true;
        _caseDetails.ReadOnly = true;
        _caseDetails.ScrollBars = ScrollBars.Vertical;
        _caseDetails.Text = "Select a case to compare the complete expected and actual actions.";
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        details.Controls.Add(_categories, 0, 0);
        details.Controls.Add(_caseDetails, 1, 0);
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
            $"{report.Cases.Count}/{report.TotalCases} cases. Partial report: {report.AutoSavePath}";
        _correctness.Text = $"Action extraction: {summary.Passed} passed, {summary.Failed} failed" +
            (summary.CorrectnessPercent is double percentage ? $" ({percentage:0.#}%)" : "");
        _speed.Text = $"Speed: cold load {Format(report.ColdLoadMs, "ms")}; " +
            $"median {Format(summary.MedianResponseMs, "ms")}; p90 {Format(summary.P90ResponseMs, "ms")}; " +
            $"prompt {Format(summary.PromptTokensPerSecond, "tok/s")}; " +
            $"generation {Format(summary.GenerationTokensPerSecond, "tok/s")}";
        _metadata.Text = $"Tag {_model} · quantization {report.Quantization} · context {report.ContextSetting}" +
            Environment.NewLine + $"Pack {report.TestPackVersion} · placement {report.Placement}";
        _categories.Text = "Category scores (action extraction; overlapping tags): " +
            string.Join("  ·  ", summary.Categories.Select(pair =>
                $"{pair.Key} {pair.Value.Passed}/{pair.Value.Total}")) +
            (report.Warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, report.Warnings));
    }

    private static string Format(double? value, string unit) => value is double number
        ? $"{number:0.#} {unit}" : "not reported";

    private void ShowSelectedCase()
    {
        if (_report is null || _cases.CurrentRow is null ||
            _cases.CurrentRow.Index >= _report.Cases.Count) return;
        var item = _report.Cases[_cases.CurrentRow.Index];
        _caseDetails.Text = $"{item.Name}: {item.Utterance}{Environment.NewLine}" +
            $"Expected: {item.ExpectedAction.GetRawText()}{Environment.NewLine}" +
            $"Actual: {item.ActualAction?.GetRawText() ?? "none / malformed"}{Environment.NewLine}" +
            $"Reason: {string.Join("; ", item.FailureReasons)}";
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
        if (disposing) _cancelRun.Dispose();
        base.Dispose(disposing);
    }
}
