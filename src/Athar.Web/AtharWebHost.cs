using MudBlazor.Services;
using Athar.Data;
using Athar.Web.Auth;
using Athar.Web.CollabBridge;
using Athar.Web.Components;
using Microsoft.EntityFrameworkCore;
using Athar.Web.Api;
using Athar.Web.Mcp;
using Athar.Web.Components.Wiki;
using Athar.Web.Services;
using Athar.Web.Sharing;

namespace Athar.Web;

/// <summary>
/// What a host that builds on Athar adds to it: services, a schema step, endpoints and the assemblies that hold its pages.
/// Athar's own host passes none.
/// </summary>
public sealed class AtharEdition
{
    /// <summary>Registers the edition's services, after Athar's own.</summary>
    public Action<WebApplicationBuilder>? ConfigureServices { get; init; }

    /// <summary>Brings the edition's own schema up to date. Runs after Athar's migration, and after a <c>db reset</c>.</summary>
    public Func<IServiceProvider, Task>? MigrateAsync { get; init; }

    /// <summary>Maps the edition's endpoints, after Athar's API and before its pages.</summary>
    public Action<WebApplication>? MapEndpoints { get; init; }

    /// <summary>Assemblies whose routable components (<c>@page</c>) join Athar's router.</summary>
    public IReadOnlyList<System.Reflection.Assembly> PageAssemblies { get; init; } = [];
}

/// <summary>Athar's web application: <c>Program.cs</c> calls this, and so does a host that builds on Athar.</summary>
public static class AtharWebHost
{
    public static async Task RunAsync(string[] args, AtharEdition? edition = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.Services.AddAtharData(
            builder.Configuration.GetConnectionString(AtharData.ConnectionName)
                ?? throw new InvalidOperationException("ConnectionStrings:athar is not set. Run through the AppHost, or pass --ConnectionStrings:athar=<connection string>."),
            // Seeded vectors carry the active embedding model id, so search accepts them under the gateway too (T100).
            new SeedOptions(builder.Configuration["Athar:Seed"] ?? "minimal",
                Athar.Domain.Memory.AiModels.EmbeddingId(builder.Configuration.GetSection("Athar:Ai").Get<Athar.Domain.Memory.AiOptions>() ?? new())));

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddAtharAuth();
        builder.Services.AddAtharOAuth(builder.Configuration);
        builder.Services.AddMudServices();
        Athar.Web.Components.AtharTheme.ApplyDefaults();
        builder.Services.AddTransient<MudBlazor.MudLocalizer, Athar.Web.AtharMudLocalizer>();
        builder.Services.AddCollabBridge();
        builder.Services.AddSharing();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<PageService>();
        builder.Services.AddScoped<HelpService>(); // T144: /help and the getting-started action
        builder.Services.AddScoped<PageCopyService>(); // T117 (U-5)
        builder.Services.AddScoped<Athar.Web.Services.Import.WikiImportService>(); // T155 (FR-PG-09)
        builder.Services.AddScoped<Athar.Web.Services.Import.WikiImportQueue>(); // T164: imports up to 4 GB run as jobs
        builder.Services.AddHostedService<Athar.Web.Services.Import.WikiImportRunner>();
        builder.Services.AddScoped<Athar.Web.Services.Import.WikiExportService>(); // T157 (FR-PG-10)
        builder.Services.AddScoped<FavoriteService>();
        builder.Services.AddScoped<Athar.Web.Services.Dashboard.DashboardService>(); // T178 (FR-AG-11)
        builder.Services.AddScoped<WikiSession>();
        builder.Services.AddScoped<Athar.Web.Services.ShellCounts>();
        builder.Services.AddScoped<Athar.Web.Services.TourService>();
        builder.Services.AddAtharMcp();
        builder.Services.AddAttachments(); // Lane D: FR-PG-06 (T046)
        Athar.Data.AtharKeys.AddAtharKeys(builder.Services, builder.Configuration); // one key ring with the worker (T070: source PATs)
        builder.Services.AddScoped<Athar.Data.AiSettingsResolver>();
        builder.Services.AddScoped<Athar.Web.Auth.AiSettingsService>();
        builder.Services.AddScoped<Athar.Web.Services.QueryAnswerService>();
        builder.Services.AddScoped<Athar.Web.Services.SourceService>(); // Lane D: GitHub sources (T070)

        // A host that builds on Athar adds its own services here.
        builder.Services.AddSingleton(edition ?? new AtharEdition());
        edition?.ConfigureServices?.Invoke(builder);

        var app = builder.Build();

        // OAuth over plain HTTP is for the local Docker stack (environment Local) only: a stray setting never turns it on in production.
        if (app.Environment.IsProduction() && app.Configuration.GetValue<bool>("Athar:OAuth:AllowHttp"))
            throw new InvalidOperationException("Athar:OAuth:AllowHttp is refused in Production: OAuth requires HTTPS there.");
        // The qa and perf seeds create users and tokens whose secrets are public (Demo-Credentials.md). Production seeds minimal only;
        // the local and perf stacks run as their own environments.
        if (app.Environment.IsProduction() && (app.Configuration["Athar:Seed"] ?? "minimal") != "minimal")
            throw new InvalidOperationException($"Athar:Seed={app.Configuration["Athar:Seed"]} is refused in Production: only the minimal seed runs there.");

        // EF owns the schema: migrate (and seed the configured profile) before serving. db reset drops, migrates and seeds, then exits.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtharDbContext>();
            if (args is ["db", "reset", ..])
            {
                // OD-5 (T102): production data is preserved. A scratch or perf stack can still opt in explicitly.
                if (app.Environment.IsProduction() && !args.Contains("--destroy-data"))
                {
                    Console.Error.WriteLine("Refusing db reset in Production: the database is data-preserving (OD-5). Add --destroy-data only on a scratch or perf stack.");
                    Environment.ExitCode = 1;
                    return;
                }
                await db.ResetAsync();
                if (edition?.MigrateAsync is { } afterReset) await afterReset(app.Services);
                Console.WriteLine("Database reset: dropped, migrated and seeded.");
                return;
            }
            await db.Database.MigrateAsync();

            // FR-ID-01: dotnet run --project src/Athar.Web -- bootstrap --email owner@example.com
            if (args is ["bootstrap", "--email", var email, ..])
            {
                try
                {
                    var owner = await AccountService.BootstrapAsync(db, email);
                    var link = await scope.ServiceProvider.GetRequiredService<AccountService>().SetPasswordLinkAsync(owner);
                    Console.WriteLine($"Bootstrap complete. {email} is the owner. Set the password (one use): {link}");
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine($"Bootstrap failed: {ex.Message}");
                    Environment.ExitCode = 1;
                }
                return;
            }
        }

        if (edition?.MigrateAsync is { } migrate) await migrate(app.Services);

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        // The internal collab API is plain HTTP on the internal network (docs/06 §6.4), so it isn't redirected.
        app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/internal"), b => b.UseHttpsRedirection());

        app.UseAtharAuth();
        app.UseAntiforgery();

        app.MapDefaultEndpoints();
        app.MapCollabBridge();
        app.MapSharing();
        app.MapAtharMcp();
        app.MapAdminApi();
        Athar.Web.Services.Import.WikiExportEndpoints.MapWikiExport(app); // T157
        Athar.Web.Services.Import.WikiImportEndpoints.MapWikiImport(app); // T164
        Athar.Web.Auth.ProfileService.MapAvatars(app);
        app.MapAtharOAuth();
        app.MapAtharApi();
        edition?.MapEndpoints?.Invoke(app);
        app.MapAgentSetup();
        app.MapStaticAssets();
        // Portal URLs renamed in T150: bookmarks, emails and old notifications keep working. /review opens its first tab.
        foreach (var (from, to) in new[] { ("/inbox", "/review/inbox"), ("/proposals", "/review/proposals"), ("/connect", "/agents"), ("/admin/workspaces", "/workspaces") })
            app.MapGet(from, () => Results.Redirect(to, permanent: true));
        // /review opens the first tab with something waiting (inbox, then proposals, then conflicts); the inbox when nothing is.
        app.MapGet("/review", async (HttpContext http, ShellCounts counts) =>
        {
            if (http.User.FindFirst(AtharClaims.Org) is null) return Results.Redirect("/review/inbox");
            await counts.RefreshAsync(Actor.ForUser(http.User), http.RequestAborted);
            return Results.Redirect(counts.Inbox == 0 && counts.Proposals > 0 ? "/review/proposals"
                : counts.Inbox == 0 && counts.Conflicts > 0 ? "/review/conflicts" : "/review/inbox");
        });
        app.MapGet("/admin/spaces/{id:guid}", (Guid id) => Results.Redirect($"/wiki/s/{id}/settings", permanent: true));
        var pages = app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        if (edition is { PageAssemblies.Count: > 0 }) pages.AddAdditionalAssemblies([.. edition.PageAssemblies]);

        await app.RunAsync();
    }
}
