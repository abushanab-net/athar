using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;

namespace Athar.Web.Api;

/// <summary>REST <c>/api/v1</c> (FR-AG-02): thin endpoints over the same services as MCP.</summary>
public static class Rest
{
    /// <summary>
    /// The OpenAPI document for <c>/api/v1</c> (docs/06 §6.3), served at <c>/openapi/v1.json</c>. Athar's request and response
    /// types are snake_case on the wire (<see cref="McpEndpoint.Json"/>), so their schemas are too.
    /// </summary>
    public static IServiceCollection AddAtharOpenApi(this IServiceCollection services) => services.AddOpenApi(o =>
    {
        o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1", StringComparison.Ordinal) == true;
        o.AddSchemaTransformer((schema, ctx, _) =>
        {
            if (ctx.JsonTypeInfo.Type.Namespace?.StartsWith("Athar.", StringComparison.Ordinal) == true && schema.Properties is { Count: > 0 } props)
            {
                foreach (var (name, value) in props.ToList())
                {
                    props.Remove(name);
                    props[System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name)] = value;
                }
                if (schema.Required is { Count: > 0 } required)
                    schema.Required = required.Select(System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName).ToHashSet();
            }
            return Task.CompletedTask;
        });
    });

    public static void MapAtharApi(this WebApplication app)
    {
        app.MapOpenApi("/openapi/{documentName}.json");
        // Bearer only: a PAT or an OAuth access token (docs/03 §2, T031).
        var v1 = app.MapGroup("/api/v1").RequireAuthorization(Auth.Bearer.Policy);
        v1.MapBrainToolEndpoints(); // contract §6.2 tools (T054)
        v1.MapPageToolEndpoints(); // contract §6.2 page tools, over Lane B and Lane D services (T054)
        v1.MapProposalEndpoints();
        v1.MapProjectEndpoints();
        v1.MapMemoryEndpoints();
        v1.MapSessionEndpoints(); // Lane D: capture hook (T060)
        v1.MapAttachmentEndpoints(); // Lane D: uploads (T046)
        v1.MapPromotionEndpoints(); // Lane D: promote and conflicts (T062)
        v1.MapSkillEndpoints(); // Lane D: skills library (T063)
        v1.MapSourceEndpoints(); // Lane D: GitHub sources (T070)
        app.MapAttachmentDownloads(); // cookie or bearer, outside /api/v1
        v1.MapOperationEndpoints(); // Lane B: durable writes (T040)
    }

    /// <summary>Runs a use case and maps domain errors to RFC 9457 problem details carrying the §6.6 code.</summary>
    public static async Task<IResult> Run<T>(Func<Task<T>> call)
    {
        try { return Results.Json(await call(), McpEndpoint.Json); }
        catch (AtharError e)
        {
            var status = e.Code switch
            {
                "invalid_input" => 400,
                "unauthorized" => 401,
                "forbidden" or "not_overridable" or "policy_denied" => 403,
                "not_found" => 404,
                "conflict_revision" or "op_id_reused" => 409,
                "pending" => 202,
                "rate_limited" => 429,
                _ => 500,
            };
            return Results.Problem(title: e.Code, detail: e.Message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = e.Code });
        }
    }

    /// <summary>For use cases with no result body.</summary>
    public static Task<IResult> Run(Func<Task> call) => Run(async () => { await call(); return new { ok = true }; });
}
