using Dapper;
using Lims.Contracts;
using Lims.Instruments.Astm;
using Lims.Messaging;
using Lims.Platform;

namespace Lims.Instruments;

public sealed class PostgresFrameJournal(Db db, InboxStore inbox, StorageGate storage) : IFrameJournal
{
    public async ValueTask<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (!storage.AcceptingNewMessages) return false;
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteScalarAsync<int>("SELECT 1");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task AppendFrameAsync(string instrumentId, LinkFrame f, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await InsertFrameAsync(conn, null, instrumentId, f, "open", null);
    }

    public Task<Guid> CommitMessageAsync(string instrumentId, LinkFrame lastFrame, byte[] message, string correlationId, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var (id, _) = await inbox.InsertAsync(conn, tx, MessageSources.Instrument, instrumentId, InboxKinds.AstmMessage, message, correlationId);
            await InsertFrameAsync(conn, tx, instrumentId, lastFrame, "assembled", id);
            await conn.ExecuteAsync("""
                UPDATE instruments.link_frame SET status='assembled', inbox_message_id=@id
                WHERE instrument_id=@instrumentId AND session_id=@s AND status='open'
                """, new { id, instrumentId, s = lastFrame.SessionId }, tx);
            return id;
        }, ct);

    public async Task MarkIncompleteAsync(string instrumentId, Guid sessionId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE instruments.link_frame SET status='incomplete' WHERE instrument_id=@instrumentId AND session_id=@sessionId AND status='open'
            """, new { instrumentId, sessionId });
    }

    public async Task RecordOutboundAsync(string instrumentId, string kind, byte[] message, string correlationId, string outcome, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO instruments.outbound_message (id, instrument_id, kind, raw, correlation_id, sent_at, outcome)
            VALUES (@id, @instrumentId, @kind, @message, @correlationId, now(), @outcome)
            """, new { id = Guid.CreateVersion7(), instrumentId, kind, message, correlationId, outcome });
    }

    private static Task InsertFrameAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction? tx, string instrumentId, LinkFrame f, string status, Guid? inboxId) =>
        conn.ExecuteAsync("""
            INSERT INTO instruments.link_frame (instrument_id, session_id, frame_index, frame_number, text, is_final, status, inbox_message_id)
            VALUES (@instrumentId, @SessionId, @FrameIndex, @FrameNumber, @Text, @IsFinal, @status, @inboxId)
            """, new { instrumentId, f.SessionId, f.FrameIndex, FrameNumber = (short)f.FrameNumber, f.Text, f.IsFinal, status, inboxId }, tx);
}
