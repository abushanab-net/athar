using System.Diagnostics;

var builder = DistributedApplication.CreateBuilder(args);

// ponytail: no data volume; G1-b migrates and seeds at startup, and `db reset` is the reset path anyway.
var db = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "pg17")
    .AddDatabase("athar");

// Image pinned here; model digests are pinned in G1-f (docs/03 D8). The volume keeps pulled models across runs.
var ollama = builder.AddOllama("ollama")
    .WithImageTag("0.34.4")
    .WithDataVolume();

// Pinned (CI pre-pulls this exact tag, .github/workflows/ci.yml), so a toolkit upgrade can't swap it unseen.
var mailpit = builder.AddMailPit("mailpit").WithImageTag("v1.31.2");

// Port 7100 comes from the web project's launch profile (https://localhost:7100).
var web = builder.AddProject<Projects.Athar_Web>("web")
    .WithReference(db).WaitFor(db)
    .WithReference(ollama)
    .WithReference(mailpit)
    .WithHttpHealthCheck("/health");
// Lane D (T081): `dotnet run --project src/Athar.AppHost -- --Athar:Seed=perf` seeds web with the perf profile (default: qa).
if (builder.Configuration["Athar:Seed"] is { Length: > 0 } seed) web.WithEnvironment("Athar__Seed", seed);

// Dashboard button for `dotnet run --project src/Athar.Web -- db reset` with this run's connection string.
web.WithCommand("db-reset", "Reset database", async ctx =>
{
    var start = new ProcessStartInfo("dotnet", ["run", "--no-build", "--project", Path.Combine(builder.AppHostDirectory, "../Athar.Web"), "--", "db", "reset"]);
    start.Environment["ConnectionStrings__athar"] = await db.Resource.ConnectionStringExpression.GetValueAsync(ctx.CancellationToken);
    using var reset = Process.Start(start)!;
    await reset.WaitForExitAsync(ctx.CancellationToken);
    return reset.ExitCode == 0 ? CommandResults.Success() : CommandResults.Failure($"db reset exited with code {reset.ExitCode}; see the AppHost console.");
}, new CommandOptions { ConfirmationMessage = "Drop the database, migrate and seed it again?", IconName = "DatabaseArrowDown" });

// Hocuspocus sidecar (Lane B). Aspire runs `npm install`, then `npm run dev` with PORT set.
// It calls web's internal collab API over plain HTTP with a shared secret (docs/06 §6.4).
var collabSecret = builder.AddParameter("collab-secret", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true);
var collab = builder.AddJavaScriptApp("collab", "../collab")
    .WithHttpEndpoint(env: "PORT")
    .WithHttpHealthCheck("/")
    .WithEnvironment("WEB_URL", web.GetEndpoint("http"))
    .WithEnvironment("COLLAB_SECRET", collabSecret);
web.WithReference(collab.GetEndpoint("http"))
    .WithEnvironment("Collab__Secret", collabSecret);

var worker = builder.AddProject<Projects.Athar_Worker>("worker")
    .WithReference(db).WaitFor(db)
    .WaitFor(web) // web applies the migrations
    .WithReference(ollama);
// T100: how often an edited page is re-indexed (tests set 0); the worker's default otherwise.
if (builder.Configuration["Athar:Index:PageEvery"] is { Length: > 0 } pageEvery) worker.WithEnvironment("Athar__Index__PageEvery", pageEvery);

// AI model gateway (T090, docs/03 D8): the on-prem model gateway when the `ai-key` secret is set (user-secrets in dev:
// `dotnet user-secrets set Parameters:ai-key <key> --project src/Athar.AppHost`), else the local Ollama models.
// Base URL and model ids are in appsettings (`Athar:Ai`); the key never is. Tests opt in (AtharApp).
if (!string.IsNullOrEmpty(builder.Configuration["Parameters:ai-key"]))
{
    var aiKey = builder.AddParameter("ai-key", secret: true);
    foreach (var app in new IResourceBuilder<IResourceWithEnvironment>[] { web, worker })
    {
        app.WithEnvironment("Athar__Ai__ApiKey", aiKey);
        foreach (var setting in builder.Configuration.GetSection("Athar:Ai").GetChildren())
            app.WithEnvironment($"Athar__Ai__{setting.Key}", setting.Value);
    }
}

builder.Build().Run();
