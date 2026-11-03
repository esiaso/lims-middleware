using Dapper;
using Npgsql;

namespace Lims.Clinical;

/// <summary>
/// Delta check: a numeric result is compared with the same patient's most recent released result for
/// the same test, collected within the test's window. When the change exceeds the catalog's percent or absolute limit,
/// the result is flagged and approval needs a review comment confirming it (for example: "checked, patient on treatment").
/// </summary>
public static class DeltaCheck
{
    public sealed class Outcome
    {
        public bool Flagged { get; init; }
        public Guid? PreviousId { get; init; }
        public decimal? PreviousValue { get; init; }
    }

    /// <summary>Pure rule, shared by the database check and the unit tests.</summary>
    public static bool Exceeds(decimal current, decimal previous, decimal? percentLimit, decimal? absoluteLimit)
    {
        var change = Math.Abs(current - previous);
        if (absoluteLimit is { } abs && change > abs) return true;
        if (percentLimit is { } pct)
        {
            if (previous == 0) return change > 0;
            if (change / Math.Abs(previous) * 100m > pct) return true;
        }
        return false;
    }

    /// <summary>Runs the check for a just-inserted result and stores the outcome on it.</summary>
    public static async Task<Outcome> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid resultId)
    {
        var row = await conn.QuerySingleOrDefaultAsync<Row>("""
            WITH cur AS (
                SELECT r.id, r.result_group, r.numeric_value, r.comparator, o.patient_id, ot.catalog_code, COALESCE(s.collected_at, r.created_at) AS at
                FROM clinical.result r
                JOIN clinical.specimen_test st ON st.id = r.specimen_test_id
                JOIN clinical.specimen s ON s.id = st.specimen_id
                JOIN clinical.order_test ot ON ot.id = st.order_test_id
                JOIN clinical.lab_order o ON o.id = ot.order_id
                WHERE r.id = @resultId
            )
            SELECT cur.numeric_value AS Current, c.delta_percent AS PercentLimit, c.delta_absolute AS AbsoluteLimit,
                   prev.id AS PreviousId, prev.numeric_value AS PreviousValue
            FROM cur
            JOIN clinical.test_catalog c ON c.code = cur.catalog_code AND c.status = 'active'
            CROSS JOIN LATERAL (
                SELECT p.id, p.numeric_value
                FROM clinical.result p
                JOIN clinical.specimen_test pst ON pst.id = p.specimen_test_id
                JOIN clinical.specimen ps ON ps.id = pst.specimen_id
                JOIN clinical.order_test pot ON pot.id = pst.order_test_id
                JOIN clinical.lab_order po ON po.id = pot.order_id
                WHERE po.patient_id = cur.patient_id AND pot.catalog_code = cur.catalog_code
                  AND p.status = 'approved' AND p.numeric_value IS NOT NULL AND p.comparator IS NULL
                  AND p.result_group <> cur.result_group
                  AND COALESCE(ps.collected_at, p.created_at) BETWEEN cur.at - make_interval(days => c.delta_window_days) AND cur.at
                ORDER BY COALESCE(ps.collected_at, p.created_at) DESC
                LIMIT 1
            ) prev
            WHERE cur.numeric_value IS NOT NULL AND cur.comparator IS NULL
              AND (c.delta_percent IS NOT NULL OR c.delta_absolute IS NOT NULL)
            """, new { resultId }, tx);
        if (row is null) return new Outcome();

        var flagged = Exceeds(row.Current, row.PreviousValue, row.PercentLimit, row.AbsoluteLimit);
        await conn.ExecuteAsync(
            "UPDATE clinical.result SET delta_flag=@flagged, delta_previous_id=@PreviousId, delta_previous_value=@PreviousValue WHERE id=@resultId",
            new { flagged, row.PreviousId, row.PreviousValue, resultId }, tx);
        return new Outcome { Flagged = flagged, PreviousId = row.PreviousId, PreviousValue = row.PreviousValue };
    }

    private sealed class Row
    {
        public decimal Current { get; init; }
        public decimal? PercentLimit { get; init; }
        public decimal? AbsoluteLimit { get; init; }
        public Guid PreviousId { get; init; }
        public decimal PreviousValue { get; init; }
    }
}
