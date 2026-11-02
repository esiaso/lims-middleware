using Dapper;
using Lims.Audit;
using Lims.Platform;
using Microsoft.AspNetCore.Identity;

namespace Lims.Host.Identity;

public sealed class UserAccount
{
    public Guid Id { get; init; }
    public string Username { get; init; } = default!;
    public string DisplayName { get; init; } = default!;
    public string PasswordHash { get; init; } = default!;
    public string[] Roles { get; init; } = default!;
    public string? TotpSecret { get; init; }
    public bool MfaEnrolled { get; init; }
    public int FailedLogins { get; init; }
    public DateTime? LockedUntil { get; init; }
    public bool IsActive { get; init; }
}

public enum LoginStatus { Ok, Invalid, LockedOut, MfaRequired, MfaEnrollmentRequired, MfaInvalid }

public sealed record LoginResult(LoginStatus Status, UserAccount? User = null, string? NewTotpSecret = null);

public sealed class PasswordPolicy
{
    public int MinLength { get; set; } = 12;
    public int MaxFailedAttempts { get; set; } = 5;
    public TimeSpan Lockout { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Local accounts (no directory service, decision E2) with PBKDF2 password hashing and TOTP MFA for
/// privileged roles (design 9.2). Lockout after 5 failed attempts.
/// </summary>
public sealed class UserStore(Db db, AuditWriter audit, IClock clock, PasswordPolicy policy)
{
    private readonly PasswordHasher<string> _hasher = new();

    private const string Select = """
        SELECT id, username, display_name AS DisplayName, password_hash AS PasswordHash, roles, totp_secret AS TotpSecret,
               mfa_enrolled AS MfaEnrolled, failed_logins AS FailedLogins, locked_until AS LockedUntil, is_active AS IsActive
        FROM platform.user_account
        """;

    public async Task<UserAccount?> FindAsync(string username, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserAccount>(Select + " WHERE username=@username", new { username });
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM platform.user_account");
    }

    public void ValidatePassword(string password)
    {
        if (password.Length < policy.MinLength) throw new WorkflowException($"Password must be at least {policy.MinLength} characters");
    }

    public async Task<Guid> CreateAsync(string username, string displayName, string password, string[] roles, string actor, CancellationToken ct)
    {
        ValidatePassword(password);
        var bad = roles.Except(Roles.All).ToList();
        if (bad.Count > 0) throw new WorkflowException($"Unknown roles: {string.Join(", ", bad)}");
        var id = Guid.CreateVersion7();
        await db.InTransactionAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync("""
                INSERT INTO platform.user_account (id, username, display_name, password_hash, roles) VALUES (@id, @username, @displayName, @hash, @roles)
                """, new { id, username, displayName, hash = _hasher.HashPassword(username, password), roles }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(actor, "user.created", "user_account", id.ToString(), After: new { username, roles }));
        }, ct);
        return id;
    }

    public async Task<LoginResult> SignInAsync(string username, string password, string? totpCode, string? address, CancellationToken ct)
    {
        var u = await FindAsync(username, ct);
        if (u is null || !u.IsActive)
        {
            await audit.WriteAsync(db, new AuditEntry(username, "auth.failed", "user_account", null, ActorAddress: address, Reason: "unknown or inactive user"), ct);
            return new(LoginStatus.Invalid);
        }
        if (u.LockedUntil is { } lu && lu > clock.UtcNow.UtcDateTime) return new(LoginStatus.LockedOut);

        var verify = _hasher.VerifyHashedPassword(u.Username, u.PasswordHash, password);
        if (verify == PasswordVerificationResult.Failed)
        {
            await RecordFailureAsync(u, address, "bad password", ct);
            return new(LoginStatus.Invalid);
        }

        var needsMfa = u.Roles.Intersect(Roles.RequireMfa).Any();
        if (needsMfa)
        {
            if (!u.MfaEnrolled)
            {
                // First sign-in: issue a secret; enrollment completes when a valid code is entered.
                var secret = u.TotpSecret ?? Totp.NewSecret();
                if (u.TotpSecret is null)
                {
                    await using var conn = await db.OpenAsync(ct);
                    await conn.ExecuteAsync("UPDATE platform.user_account SET totp_secret=@secret WHERE id=@id", new { secret, id = u.Id });
                }
                if (string.IsNullOrWhiteSpace(totpCode) || !Totp.Verify(secret, totpCode, clock.UtcNow))
                    return new(LoginStatus.MfaEnrollmentRequired, u, secret);
                await using (var conn = await db.OpenAsync(ct))
                    await conn.ExecuteAsync("UPDATE platform.user_account SET mfa_enrolled=true WHERE id=@id", new { id = u.Id });
                await audit.WriteAsync(db, new AuditEntry(u.Username, "auth.mfa_enrolled", "user_account", u.Id.ToString(), ActorAddress: address), ct);
            }
            else if (string.IsNullOrWhiteSpace(totpCode))
            {
                return new(LoginStatus.MfaRequired, u);
            }
            else if (!Totp.Verify(u.TotpSecret!, totpCode, clock.UtcNow))
            {
                await RecordFailureAsync(u, address, "bad MFA code", ct);
                return new(LoginStatus.MfaInvalid, u);
            }
        }

        await using (var conn = await db.OpenAsync(ct))
        {
            await conn.ExecuteAsync("UPDATE platform.user_account SET failed_logins=0, locked_until=NULL WHERE id=@id", new { id = u.Id });
            if (verify == PasswordVerificationResult.SuccessRehashNeeded)
                await conn.ExecuteAsync("UPDATE platform.user_account SET password_hash=@h WHERE id=@id", new { h = _hasher.HashPassword(u.Username, password), id = u.Id });
        }
        await audit.WriteAsync(db, new AuditEntry(u.Username, "auth.signed_in", "user_account", u.Id.ToString(), ActorAddress: address), ct);
        return new(LoginStatus.Ok, u);
    }

    private async Task RecordFailureAsync(UserAccount u, string? address, string reason, CancellationToken ct)
    {
        await db.InTransactionAsync(async (conn, tx) =>
        {
            var failed = await conn.ExecuteScalarAsync<int>(
                "UPDATE platform.user_account SET failed_logins = failed_logins + 1 WHERE id=@id RETURNING failed_logins", new { id = u.Id }, tx);
            if (failed >= policy.MaxFailedAttempts)
                await conn.ExecuteAsync("UPDATE platform.user_account SET locked_until = now() + @lockout, failed_logins = 0 WHERE id=@id",
                    new { id = u.Id, lockout = policy.Lockout }, tx);
            await audit.WriteAsync(conn, tx, new AuditEntry(u.Username, failed >= policy.MaxFailedAttempts ? "auth.locked_out" : "auth.failed",
                "user_account", u.Id.ToString(), ActorAddress: address, Reason: reason));
        }, ct);
    }
}
