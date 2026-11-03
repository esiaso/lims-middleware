using System.Globalization;
using System.Text;
using Lims.Contracts;
using Lims.Instruments;
using Lims.Instruments.Generic;
using Lims.Instruments.Registration;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

/// <summary>
/// Analyzer setup: register an analyzer that has no built-in adapter, change one, disable one. A Lab Manager proposes,
/// a different Lab Manager approves (maker-checker, audited). A sample message can be checked against the layout
/// before proposing, without saving anything.
/// </summary>
public sealed class AnalyzerSetupModel(AnalyzerDefinitionService definitions, InstrumentDirectory directory) : PageModel
{
    public IReadOnlyList<AnalyzerDefinitionRow> Definitions { get; private set; } = [];
    public IReadOnlyList<InstrumentConfig> Configured => directory.Configured;
    [BindProperty(SupportsGet = true)] public bool History { get; set; }
    [BindProperty] public AnalyzerDefinition Definition { get; set; } = new();
    [BindProperty] public string UtcOffsetText { get; set; } = "+03:00";
    [BindProperty] public string? IgnoredTestCodes { get; set; }
    [BindProperty] public string? QcSampleIdPrefixes { get; set; }
    [BindProperty] public string RerunStatusValues { get; set; } = "C";
    [BindProperty] public string NormalFlagValues { get; set; } = "N";
    [BindProperty] public string TimestampFormats { get; set; } = "yyyyMMddHHmmss, yyyyMMddHHmm, yyyyMMdd";
    [BindProperty] public int ReceiveTimeoutSeconds { get; set; } = 15;
    [BindProperty] public int AckTimeoutSeconds { get; set; } = 15;
    [BindProperty] public string? SampleMessage { get; set; }
    public IReadOnlyList<string> Errors { get; private set; } = [];
    public ParseOutcome? Preview { get; private set; }
    public string? Message { get; private set; }
    public bool CanEdit => User.IsInRole(Roles.LabManager);

    public async Task<IActionResult> OnGetAsync(Guid? edit, CancellationToken ct)
    {
        if (!CanView()) return Forbid();
        if (edit is { } id && await definitions.GetAsync(id, ct) is { } row) Fill(row.Parsed);
        else Fill(new AnalyzerDefinition());
        Message = TempData["Message"] as string;
        await LoadAsync(ct);
        return Page();
    }

    private bool CanView() => User.IsInRole(Roles.LabManager) || User.IsInRole(Roles.IntegrationOperator) || User.IsInRole(Roles.Administrator);

    private async Task LoadAsync(CancellationToken ct) => Definitions = await definitions.ListAsync(History, ct);

    private void Fill(AnalyzerDefinition d)
    {
        Definition = d;
        UtcOffsetText = (d.UtcOffset < TimeSpan.Zero ? "-" : "+") + d.UtcOffset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        IgnoredTestCodes = string.Join(", ", d.Profile.IgnoredTestCodes);
        QcSampleIdPrefixes = string.Join(", ", d.Profile.QcSampleIdPrefixes);
        RerunStatusValues = string.Join(", ", d.Profile.RerunStatusValues);
        NormalFlagValues = string.Join(", ", d.Profile.NormalFlagValues);
        TimestampFormats = string.Join(", ", d.Profile.TimestampFormats);
        ReceiveTimeoutSeconds = (int)d.Link.ReceiveTimeout.TotalSeconds;
        AckTimeoutSeconds = (int)d.Link.AckTimeout.TotalSeconds;
    }

    /// <summary>The definition as the form describes it; list fields are comma-separated.</summary>
    private AnalyzerDefinition Collect()
    {
        var d = Definition;
        d.InstrumentId = (d.InstrumentId ?? "").Trim().ToLowerInvariant();
        d.Model = (d.Model ?? "").Trim();
        d.SoftwareVersion = string.IsNullOrWhiteSpace(d.SoftwareVersion) ? null : d.SoftwareVersion.Trim();
        d.Protocol = (d.Protocol ?? "astm").Trim().ToLowerInvariant();
        var offset = (UtcOffsetText ?? "").Trim();
        var negative = offset.StartsWith('-');
        d.UtcOffset = TimeSpan.TryParseExact(offset.TrimStart('+', '-'), [@"hh\:mm", @"h\:mm", "hh", "%h"], CultureInfo.InvariantCulture, out var ts)
            ? (negative ? -ts : ts)
            : TimeSpan.FromHours(99); // rejected by validation
        d.Profile.IgnoredTestCodes = List(IgnoredTestCodes);
        d.Profile.QcSampleIdPrefixes = List(QcSampleIdPrefixes);
        d.Profile.RerunStatusValues = List(RerunStatusValues);
        d.Profile.NormalFlagValues = List(NormalFlagValues);
        d.Profile.TimestampFormats = List(TimestampFormats);
        d.Link.ReceiveTimeout = TimeSpan.FromSeconds(ReceiveTimeoutSeconds);
        d.Link.AckTimeout = TimeSpan.FromSeconds(AckTimeoutSeconds);
        d.Transport.Host = (d.Transport.Host ?? "").Trim();
        d.Transport.PortName = string.IsNullOrWhiteSpace(d.Transport.PortName) ? null : d.Transport.PortName.Trim();
        return d;
    }

    private static List<string> List(string? csv) =>
        (csv ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    /// <summary>Checks the form and parses the sample message with it. Nothing is saved.</summary>
    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct)
    {
        if (!CanEdit) return Forbid();
        var d = Collect();
        Errors = definitions.Check(d);
        if (Errors.Count == 0 && !string.IsNullOrWhiteSpace(SampleMessage)) Preview = PreviewParse(d, SampleMessage);
        if (Errors.Count == 0) Message = Preview is null ? "The definition is complete. Paste a captured message to see how it is read." : null;
        await LoadAsync(ct);
        return Page();
    }

    public static ParseOutcome PreviewParse(AnalyzerDefinition d, string sample)
    {
        // Pasted text: line breaks stand for record or segment terminators.
        var text = sample.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        var cfg = d.ToConfig(Guid.Empty, 0, true);
        var parser = ParserRegistry.Create(cfg)!;
        return parser.Parse(d.InstrumentId, d.Profile.TextEncoding().GetBytes(text), "preview");
    }

    public async Task<IActionResult> OnPostProposeAsync(CancellationToken ct)
    {
        if (!CanEdit) return Forbid();
        var d = Collect();
        try
        {
            await definitions.ProposeAsync(d, User.Identity!.Name!, ct);
            TempData["Message"] = $"Proposed {d.InstrumentId}. A different Lab Manager must approve it before the link starts.";
            return RedirectToPage();
        }
        catch (WorkflowException ex)
        {
            Errors = ex.Message.Split("; ");
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task<IActionResult> Run(Func<Task> action, string done, CancellationToken ct)
    {
        if (!CanEdit) return Forbid();
        try
        {
            await action();
            TempData["Message"] = done;
            return RedirectToPage(new { History });
        }
        catch (WorkflowException ex)
        {
            Message = ex.Message;
            Fill(new AnalyzerDefinition());
            await LoadAsync(ct);
            return Page();
        }
    }

    public Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken ct) =>
        Run(() => definitions.ApproveAsync(id, User.Identity!.Name!, ct), "Approved. The link starts within a few seconds.", ct);

    public Task<IActionResult> OnPostDisableAsync(string instrumentId, string reason, CancellationToken ct) =>
        Run(() => definitions.DisableAsync(instrumentId, User.Identity!.Name!, reason, ct), $"{instrumentId} disabled; its link is stopped.", ct);

    public Task<IActionResult> OnPostCopyAsync(Guid id, CancellationToken ct) =>
        Run(() => definitions.ProposeCopyAsync(id, User.Identity!.Name!, ct), "Proposed again. A different Lab Manager must approve it.", ct);

    public Task<IActionResult> OnPostWithdrawAsync(Guid id, CancellationToken ct) =>
        Run(() => definitions.WithdrawAsync(id, User.Identity!.Name!, ct), "Proposal withdrawn.", ct);

    public static string Summary(AnalyzerDefinition d)
    {
        var t = d.Transport;
        var link = t.Mode switch
        {
            TransportMode.TcpClient => $"connects to {t.Host}:{t.Port}",
            TransportMode.TcpServer => $"listens on {t.Host}:{t.Port}",
            _ => $"serial {t.PortName} {t.BaudRate} {t.DataBits}{t.Parity[..1]}{(t.StopBits == "Two" ? 2 : 1)}",
        };
        var sb = new StringBuilder($"{(d.IsHl7 ? "HL7" : "ASTM")}, {link}, worklist {d.Profile.Worklist}");
        return sb.ToString();
    }
}
