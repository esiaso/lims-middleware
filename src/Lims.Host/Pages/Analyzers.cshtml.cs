using Dapper;
using Lims.Audit;
using Lims.Instruments;
using Lims.Instruments.Astm;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Lims.Host.Pages;

public sealed record AnalyzerView(InstrumentConfig Config, LinkHealth Health, string Verification, IReadOnlyList<dynamic> Worklist,
    IReadOnlyList<dynamic> Messages, IReadOnlyList<dynamic> Qc);

/// <summary>Analyzers and worklists (design 8): link state, pending worklist, last messages (metadata only), QC today.</summary>
public sealed class AnalyzersModel(Db db, Lims.Instruments.Registration.InstrumentDirectory directory, LinkHealthRegistry links, OrderResendService resend) : PageModel
{
    public IReadOnlyList<AnalyzerView> Analyzers { get; private set; } = [];
    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var list = new List<AnalyzerView>();
        foreach (var cfg in directory.All)
        {
            var verification = await conn.ExecuteScalarAsync<string?>("SELECT verification_status FROM instruments.instrument WHERE id=@Id", cfg) ?? "unregistered";
            list.Add(new AnalyzerView(cfg, links.Get(cfg.Id), verification,
                (await PendingWorklistAsync(conn, cfg.Id)).ToList(),
                (await conn.QueryAsync("""
                    SELECT received_at, kind, status, octet_length(raw) AS bytes, last_error, correlation_id FROM messaging.inbox_message
                    WHERE source='instrument' AND channel=@Id ORDER BY received_at DESC LIMIT 15
                    """, cfg)).ToList(),
                (await conn.QueryAsync("""
                    SELECT received_at, control_name, control_lot, instrument_test_code, raw_value, units, flags FROM clinical.qc_result
                    WHERE instrument_id=@Id AND received_at >= date_trunc('day', now() AT TIME ZONE 'Africa/Nairobi') AT TIME ZONE 'Africa/Nairobi'
                    ORDER BY received_at DESC LIMIT 100
                    """, cfg)).ToList()));
        }
        Analyzers = list;
    }

    /// <summary>Accepted specimens with tests still pending on this instrument (what the load list prints).</summary>
    public static Task<IEnumerable<dynamic>> PendingWorklistAsync(Npgsql.NpgsqlConnection conn, string instrumentId) =>
        conn.QueryAsync("""
            SELECT s.accession, s.barcode, o.priority, s.received_at,
                   upper(left(COALESCE(p.given_name,''),1) || left(COALESCE(p.family_name,''),1)) AS initials,
                   extract(year FROM p.birth_date)::int AS birth_year,
                   string_agg(DISTINCT ot.catalog_code, ', ' ORDER BY ot.catalog_code) AS tests
            FROM clinical.specimen s
            JOIN clinical.lab_order o ON o.id = s.order_id
            JOIN clinical.patient p ON p.id = o.patient_id
            JOIN clinical.specimen_test st ON st.specimen_id = s.id
            JOIN clinical.order_test ot ON ot.id = st.order_test_id
            JOIN instruments.test_mapping m ON m.catalog_code = ot.catalog_code AND m.instrument_id = @instrumentId AND m.status = 'active' AND m.reportable
            WHERE s.status IN ('accepted','on_instrument') AND ot.status = 'pending_at_instrument'
            GROUP BY s.accession, s.barcode, o.priority, s.received_at, p.given_name, p.family_name, p.birth_date
            ORDER BY (o.priority = 'stat') DESC, s.received_at
            LIMIT 500
            """, new { instrumentId });

    public async Task<IActionResult> OnPostResendAsync(string instrumentId, string barcode, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Technologist) && !User.IsInRole(Roles.IntegrationOperator)) return Forbid();
        try
        {
            await resend.ResendAsync(instrumentId, barcode, User.Identity!.Name!, ct);
            Message = $"Order for {barcode} queued to {instrumentId}.";
        }
        catch (WorkflowException ex) { Message = ex.Message; }
        await LoadAsync(ct);
        return Page();
    }
}
