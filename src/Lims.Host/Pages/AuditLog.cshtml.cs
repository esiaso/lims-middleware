using Dapper;
using Lims.Audit;
using Lims.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lims.Host.Pages;

public sealed class AuditLogModel(Db db, AuditVerifier verifier, AuditWriter audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Accession { get; set; }
    [BindProperty(SupportsGet = true)] public string? Patient { get; set; }
    [BindProperty(SupportsGet = true)] public string? Actor { get; set; }
    [BindProperty(SupportsGet = true)] public string? CorrelationId { get; set; }
    [BindProperty(SupportsGet = true)] public string? EntityId { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
    public IReadOnlyList<dynamic> Rows { get; private set; } = [];
    public ChainVerification? Verification { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Auditor)) return Forbid();
        await SearchAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken ct)
    {
        if (!User.IsInRole(Roles.Auditor)) return Forbid();
        Verification = await verifier.VerifyAsync(ct);
        await audit.WriteAsync(db, new AuditEntry(User.Identity!.Name!, "audit.chain_verified", "audit_event", null,
            After: new { Verification.Valid, Verification.RowsChecked, Verification.FirstBadSeq }), ct);
        await SearchAsync(ct);
        return Page();
    }

    private async Task SearchAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        Rows = (await conn.QueryAsync("""
            SELECT seq, occurred_at, actor, action, entity_type, entity_id, accession, patient_id, reason,
                   before_json::text AS before, after_json::text AS after
            FROM audit.audit_event
            WHERE (@Accession::text IS NULL OR accession = @Accession)
              AND (@Patient::text IS NULL OR patient_id = @Patient)
              AND (@Actor::text IS NULL OR actor = @Actor)
              AND (@CorrelationId::text IS NULL OR correlation_id = @CorrelationId)
              AND (@EntityId::text IS NULL OR entity_id = @EntityId)
              AND (@From::timestamptz IS NULL OR occurred_at >= @From)
              AND (@To::timestamptz IS NULL OR occurred_at < @To + interval '1 day')
            ORDER BY seq DESC LIMIT 500
            """, new { Accession = Nz(Accession), Patient = Nz(Patient), Actor = Nz(Actor), CorrelationId = Nz(CorrelationId), EntityId = Nz(EntityId), From, To })).ToList();
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
