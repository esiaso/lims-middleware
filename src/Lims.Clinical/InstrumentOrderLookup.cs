using Dapper;
using Lims.Contracts;
using Lims.Platform;

namespace Lims.Clinical;

/// <summary>
/// Answers analyzer host queries from ACCEPTED specimens only, using the active instrument mapping
/// (catalog code -> instrument code). Unknown or not-accepted barcodes get "no order".
/// </summary>
public sealed class InstrumentOrderLookup(Db db) : IInstrumentOrderLookup
{
    public async Task<InstrumentOrder?> GetOrdersForSampleAsync(string instrumentId, string sampleId, CancellationToken ct)
    {
        var barcode = AccessionNumber.NormalizeBarcode(sampleId);
        if (barcode is null) return null;
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<(string InstrumentCode, string Priority, Guid SpecimenId)>("""
            SELECT m.instrument_code, o.priority, s.id
            FROM clinical.specimen s
            JOIN clinical.lab_order o ON o.id = s.order_id
            JOIN clinical.specimen_test st ON st.specimen_id = s.id
            JOIN clinical.order_test ot ON ot.id = st.order_test_id
            JOIN instruments.test_mapping m ON m.catalog_code = ot.catalog_code AND m.instrument_id = @instrumentId AND m.status = 'active'
            WHERE s.barcode = @barcode
              AND s.status IN ('accepted','on_instrument')
              AND ot.status IN ('pending_at_instrument')
              AND (st.instrument_id IS NULL OR st.instrument_id = @instrumentId)
            ORDER BY m.instrument_code
            """, new { instrumentId, barcode })).ToList();
        if (rows.Count == 0) return null;
        await conn.ExecuteAsync("UPDATE clinical.specimen SET status='on_instrument' WHERE id=@id AND status='accepted'", new { id = rows[0].SpecimenId });
        return new InstrumentOrder(barcode, rows[0].Priority, rows.Select(r => r.InstrumentCode).ToList());
    }
}
