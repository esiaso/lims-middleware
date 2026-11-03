using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lims.Instruments.Astm;
using Lims.Sim.E411;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lims.UnitTests;

internal sealed class FakeJournal : IFrameJournal
{
    public bool Available { get; set; } = true;
    public int FailCommits { get; set; }
    public ConcurrentQueue<string> Messages { get; } = new();
    public ConcurrentQueue<LinkFrame> Frames { get; } = new();
    public ConcurrentQueue<(string Kind, string Text, string Outcome)> Outbound { get; } = new();
    public int Incomplete;

    public ValueTask<bool> IsAvailableAsync(CancellationToken ct) => ValueTask.FromResult(Available);
    public Task AppendFrameAsync(string instrumentId, LinkFrame frame, CancellationToken ct) { Frames.Enqueue(frame); return Task.CompletedTask; }
    public Task<Guid> CommitMessageAsync(string instrumentId, LinkFrame lastFrame, byte[] message, string correlationId, CancellationToken ct)
    {
        if (FailCommits > 0) { FailCommits--; throw new InvalidOperationException("db down"); }
        Frames.Enqueue(lastFrame);
        Messages.Enqueue(Encoding.ASCII.GetString(message));
        return Task.FromResult(Guid.NewGuid());
    }
    public Task<Guid> CommitWholeMessageAsync(string instrumentId, string kind, byte[] message, string correlationId, CancellationToken ct)
    {
        if (FailCommits > 0) { FailCommits--; throw new InvalidOperationException("db down"); }
        Messages.Enqueue(Encoding.UTF8.GetString(message));
        return Task.FromResult(Guid.NewGuid());
    }
    public Task MarkIncompleteAsync(string instrumentId, Guid sessionId, CancellationToken ct) { Interlocked.Increment(ref Incomplete); return Task.CompletedTask; }
    public Task RecordOutboundAsync(string instrumentId, string kind, byte[] message, string correlationId, string outcome, CancellationToken ct)
    { Outbound.Enqueue((kind, Encoding.ASCII.GetString(message), outcome)); return Task.CompletedTask; }
}

internal sealed class FixedResponder(string? reply) : IHostQueryResponder
{
    public Task<string?> BuildReplyAsync(string instrumentId, byte[] queryMessage, string correlationId, CancellationToken ct) => Task.FromResult(reply);
}

public sealed class LinkSessionTests : IAsyncLifetime
{
    private TcpListener _listener = null!;
    private TcpClient _instrumentSide = null!;
    private TcpClient _hostSide = null!;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly FakeJournal _journal = new();
    private readonly LinkHealth _health = new() { InstrumentId = "e411-1" };
    private Task _session = Task.CompletedTask;
    private E411Simulator _sim = null!;

    private static readonly AstmLinkOptions FastOptions = new()
    {
        ReceiveTimeout = TimeSpan.FromSeconds(2), AckTimeout = TimeSpan.FromSeconds(2),
        NakRetryDelay = TimeSpan.FromMilliseconds(50), IdlePoll = TimeSpan.FromMilliseconds(50),
        ContentionRetryDelay = TimeSpan.FromMilliseconds(50),
    };

    public async Task InitializeAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _instrumentSide = new TcpClient();
        var accept = _listener.AcceptTcpClientAsync();
        await _instrumentSide.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        _hostSide = await accept;
        _sim = new E411Simulator(_instrumentSide.GetStream(), TimeSpan.FromSeconds(5));
    }

    private void StartSession(string? reply = null) =>
        _session = Task.Run(() => new AstmLinkSession("e411-1", _journal, new FixedResponder(reply), FastOptions, _health, NullLogger.Instance)
            .RunAsync(_hostSide.GetStream(), _cts.Token));

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try { await _session; } catch { /* cancelled */ }
        _instrumentSide.Dispose(); _hostSide.Dispose(); _listener.Stop();
    }

    private static string ResultMsg(string sid = "261102000427") =>
        E411Simulator.ResultMessage(sid, [new SimResult("10", "1.25", "uIU/mL")], new DateTime(2026, 11, 2, 10, 0, 0));

    [Fact]
    public async Task Valid_message_is_committed_once_then_acknowledged()
    {
        StartSession();
        Assert.Equal(SendStatus.Accepted, await _sim.SendAsync(ResultMsg()));
        var msg = Assert.Single(_journal.Messages);
        Assert.Equal(ResultMsg(), msg);
        Assert.Equal(5, _journal.Frames.Count);
        Assert.Equal(1, _health.MessagesToday);
    }

    [Fact]
    public async Task Bad_checksum_gets_NAK_and_retransmission_is_accepted()
    {
        StartSession();
        Assert.Equal(SendStatus.Accepted, await _sim.SendAsync(ResultMsg(), new SendOptions { CorruptChecksumFrame = 2 }));
        Assert.Equal(1, _sim.NaksReceived);
        Assert.Equal(ResultMsg(), Assert.Single(_journal.Messages));
        Assert.Equal(1, _health.ChecksumErrors);
    }

    [Fact]
    public async Task Duplicate_frame_after_lost_ack_is_discarded()
    {
        StartSession();
        Assert.Equal(SendStatus.Accepted, await _sim.SendAsync(ResultMsg(), new SendOptions { DuplicateFrame = 1 }));
        Assert.Equal(ResultMsg(), Assert.Single(_journal.Messages));
    }

    [Fact]
    public async Task Storage_unavailable_refuses_at_ENQ()
    {
        _journal.Available = false;
        StartSession();
        Assert.Equal(SendStatus.Refused, await _sim.SendAsync(ResultMsg()));
        Assert.Empty(_journal.Messages);
    }

    [Fact]
    public async Task Commit_failure_is_never_acknowledged()
    {
        _journal.FailCommits = 100;
        StartSession();
        Assert.Equal(SendStatus.Failed, await _sim.SendAsync(ResultMsg()));
        Assert.Empty(_journal.Messages);
        Assert.True(_health.NaksSent >= 6);
    }

    [Fact]
    public async Task Commit_failure_then_recovery_stores_exactly_once()
    {
        _journal.FailCommits = 2;
        StartSession();
        Assert.Equal(SendStatus.Accepted, await _sim.SendAsync(ResultMsg()));
        Assert.Equal(ResultMsg(), Assert.Single(_journal.Messages));
    }

    [Fact]
    public async Task Cable_pull_mid_message_keeps_frames_as_incomplete()
    {
        StartSession();
        Assert.Equal(SendStatus.Failed, await _sim.SendAsync(ResultMsg(), new SendOptions { AbortAfterFrames = 2 }));
        await WaitUntil(() => _journal.Incomplete > 0);
        Assert.Empty(_journal.Messages);
        Assert.Equal(2, _journal.Frames.Count);
    }

    [Fact]
    public async Task Host_query_is_answered_after_EOT()
    {
        var reply = "H|\\^&|||host^1|||||cobas-e411|TSDWN^REPLY|P|1\rP|1\rO|1|261102000427|40^0^5^^S1^SC|^^^10^|R||||||A||||1||||||||||O\rL|1|N\r";
        StartSession(reply);
        Assert.Equal(SendStatus.Accepted, await _sim.SendAsync(E411Simulator.QueryMessage("261102000427")));
        var received = await _sim.ReceiveAsync();
        Assert.Equal(reply, received);
        await WaitUntil(() => _journal.Outbound.Count == 1);
        Assert.Equal("Sent", _journal.Outbound.Single().Outcome);
    }

    [Fact]
    public async Task Long_message_spanning_frame_numbers_wraps_correctly()
    {
        StartSession();
        var results = Enumerable.Range(1, 12).Select(i => new SimResult($"{i}", $"{i}.5", "U/L")).ToList();
        var msg = E411Simulator.ResultMessage("261102000427", results, new DateTime(2026, 11, 2, 10, 0, 0));
        Assert.Equal(SendStatus.Accepted, await _sim.SendAsync(msg));
        Assert.Equal(msg, Assert.Single(_journal.Messages));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }
}
