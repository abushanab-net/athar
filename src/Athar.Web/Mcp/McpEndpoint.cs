using System.Text.Json;
using System.Text.Json.Serialization;
using Athar.Web.Api;
using Athar.Web.Services;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;

namespace Athar.Web.Mcp;

// MCP over Streamable HTTP at /mcp, stateless, PAT-authenticated on every request (FR-AG-01, docs/06 §6.1).
public static class McpEndpoint
{
    public static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string ProtocolVersion = "2025-11-25";

    public static IServiceCollection AddAtharMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<MemoryService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<SearchService>();
        services.AddScoped<QueryEmbedder>();
        services.AddScoped<SessionService>();
        services.AddScoped<CaptureSettingsService>();
        services.AddScoped<AgentPageTools.AgentPageService>();
        services.AddScoped<SkillService>();
        services.AddAtharOpenApi(); // REST /api/v1 shares this registration hook (Program.cs calls AddAtharMcp)
        // FR-AG-01: the protocol revision is pinned like the SDK package (Directory.Packages.props).
        services.AddMcpServer(o => o.ProtocolVersion = ProtocolVersion)
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithRequestFilters(f => f.AddCallToolFilter(McpCapture.Filter)) // FR-AG-12a, FR-AU-01
            .WithTools<MemoryTools>(Json)
            .WithTools<SessionTools>(Json)
            .WithTools<AgentPageTools.PageTools>(Json)
            .WithTools<PromotionTools>(Json)
            .WithTools<SkillTools>(Json)
            .WithTools<SourceTools>(Json) // Lane D (T142): add_source
            .WithResources<AtharResources>()
            .WithPrompts<AtharPrompts>();
        return services;
    }

    // Browsers are refused unless their Origin is on Mcp:AllowedOrigins. Agents send no Origin.
    public static void MapAtharMcp(this WebApplication app)
    {
        var allowed = app.Configuration.GetSection("Mcp:AllowedOrigins").Get<string[]>() ?? [];
        // Middleware, not an endpoint filter: MapMcp endpoints don't run endpoint filters.
        app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (!ctx.Request.Path.StartsWithSegments("/mcp") || origin.Length == 0
                || allowed.Contains(origin, StringComparer.OrdinalIgnoreCase))
                await next(ctx);
            else
                await Results.Problem(title: "forbidden", detail: "Origin not allowed", statusCode: 403).ExecuteAsync(ctx);
        });
        app.MapMcp("/mcp").RequireAuthorization(Auth.Bearer.Policy);
    }
}
