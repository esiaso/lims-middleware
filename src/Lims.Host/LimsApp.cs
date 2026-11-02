using System.Security.Claims;
using Lims.Audit;
using Lims.Clinical;
using Lims.Contracts;
using Lims.Hmis;
using Lims.Host.Api;
using Lims.Host.Identity;
using Lims.Instruments;
using Lims.Instruments.Astm;
using Lims.Messaging;
using Lims.Platform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

namespace Lims.Host;

public static class LimsApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        // Windows Service on Windows Server 2022 (decision E1); no-op elsewhere. systemd works the same way on Linux.
        builder.Host.UseWindowsService(o => o.ServiceName = "LimsMiddleware");
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
        configure?.Invoke(builder);

        var s = builder.Services;
        var cs = builder.Configuration.GetConnectionString("Lims") ?? throw new InvalidOperationException("ConnectionStrings:Lims is required");
        s.AddSingleton(_ => new NpgsqlDataSourceBuilder(cs).Build());
        s.AddSingleton<Db>();
        s.AddSingleton<IClock, SystemClock>();
        s.AddSingleton<Migrator>();

        // Audit
        s.AddSingleton<AuditWriter>();
        s.AddSingleton<AuditVerifier>();
        s.Configure<AuditExportOptions>(builder.Configuration.GetSection("AuditExport"));
        s.AddHostedService<AuditExporter>();

        // Messaging
        s.AddSingleton<InboxStore>();
        s.AddSingleton<OutboxStore>();
        s.AddSingleton<DeadLetterStore>();
        s.Configure<InboxProcessorOptions>(builder.Configuration.GetSection("Inbox"));
        s.AddSingleton<InboxProcessor>();
        s.AddHostedService(sp => sp.GetRequiredService<InboxProcessor>());

        // Clinical
        s.AddSingleton<OrderRegistrationHandler>();
        s.AddSingleton<IInboxHandler>(sp => sp.GetRequiredService<OrderRegistrationHandler>());
        s.AddSingleton<IInboxHandler, InstrumentResultHandler>();
        s.AddSingleton<SpecimenService>();
        s.AddSingleton<ResultReviewService>();
        s.AddSingleton<DowntimeService>();
        s.AddSingleton<CatalogService>();
        s.AddSingleton<ClinicalQueries>();
        s.AddSingleton<IInstrumentOrderLookup, InstrumentOrderLookup>();
        s.Configure<ReviewOptions>(builder.Configuration.GetSection("Review"));
        s.Configure<Lims.Host.Pages.LabelPrinterOptions>(builder.Configuration.GetSection("LabelPrinter"));

        // Instruments
        s.Configure<InstrumentsOptions>(builder.Configuration.GetSection("Instruments"));
        s.AddSingleton<IInstrumentParserResolver, ParserRegistry>();
        s.AddSingleton<IFrameJournal, PostgresFrameJournal>();
        s.AddSingleton<IHostQueryResponder, E411QueryResponder>();
        s.AddSingleton<LinkHealthRegistry>();
        s.AddSingleton<MappingService>();
        s.AddHostedService<AstmLinkWorker>();

        // HMIS
        s.Configure<HmisOptions>(builder.Configuration.GetSection("Hmis"));
        s.AddSingleton<HmisHealth>();
        s.AddHttpClient<HmisClient>().ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
        {
            ConnectTimeout = builder.Configuration.GetValue("Hmis:ConnectTimeout", TimeSpan.FromSeconds(10)),
        });
        s.AddSingleton<IInboxHandler, HmisOrderEventHandler>();
        s.AddHostedService<HmisOrderPoller>();
        s.AddSingleton<OutboxDispatcher>();
        s.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());

        // Identity: local accounts + TOTP for privileged roles (design 9.2)
        s.AddSingleton(builder.Configuration.GetSection("PasswordPolicy").Get<PasswordPolicy>() ?? new PasswordPolicy());
        s.AddSingleton<UserStore>();
        s.AddSingleton<HealthService>();
        s.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.LoginPath = "/Login";
            o.LogoutPath = "/Logout";
            o.ExpireTimeSpan = TimeSpan.FromMinutes(15);
            o.SlidingExpiration = true;
            o.Cookie.Name = "lims.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.Events.OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = 401; else ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = 403; else ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
        });
        s.AddAuthorization(o =>
        {
            foreach (var role in Roles.All) o.AddPolicy(role, p => p.RequireRole(role));
            o.AddPolicy("ops", p => p.RequireRole(Roles.IntegrationOperator, Roles.Administrator));
            o.AddPolicy("any", p => p.RequireAuthenticatedUser());
        });
        s.AddRazorPages(o =>
        {
            o.Conventions.AuthorizeFolder("/");
            o.Conventions.AllowAnonymousToPage("/Login");
        });
        s.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));

        var app = builder.Build();
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        ApiEndpoints.Map(app);
        return app;
    }

    /// <summary>Applies migrations and creates the bootstrap administrator when there are no users.</summary>
    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        await app.Services.GetRequiredService<Migrator>().MigrateAsync(ct);
        var users = app.Services.GetRequiredService<UserStore>();
        var cfg = app.Configuration.GetSection("Bootstrap");
        if (await users.CountAsync(ct) == 0 && cfg["AdminUsername"] is { Length: > 0 } name && cfg["AdminPassword"] is { Length: > 0 } pw)
        {
            await users.CreateAsync(name, "Bootstrap administrator", pw, [Roles.Administrator], "system:bootstrap", ct);
            app.Logger.LogWarning("Created bootstrap administrator '{User}'. Create named accounts and deactivate it (design 10.3).", name);
        }
    }

    public static string UserName(this ClaimsPrincipal p) => p.Identity?.Name ?? throw new InvalidOperationException("no user");
}
