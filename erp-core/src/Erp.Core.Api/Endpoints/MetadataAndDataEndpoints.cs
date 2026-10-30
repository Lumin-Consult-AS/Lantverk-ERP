using System.Globalization;
using System.Text.Json;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Data;
using Erp.Core.Infrastructure.Metadata;

namespace Erp.Core.Api.Endpoints;

public sealed record YamlMetadataRequest(string Yaml);

public static class MetadataAndDataEndpoints
{
    public static void MapMetadataAndDataEndpoints(this IEndpointRouteBuilder v1)
    {
        // Metadata endpoints
        v1.MapGet("/metadata/objects", async (MetadataRegistryService registry, CancellationToken ct) =>
            Results.Ok(await registry.GetObjectsAsync(ct)));

        v1.MapGet("/metadata/objects/{objectId}", async (string objectId, MetadataRegistryService registry, CancellationToken ct) =>
        {
            var obj = await registry.GetObjectAsync(objectId, ct);
            return obj is not null ? Results.Ok(obj) : Results.NotFound(new { error = $"Object '{objectId}' was not found." });
        });

        v1.MapGet("/metadata/enums", async (MetadataRegistryService registry, CancellationToken ct) =>
            Results.Ok(await registry.GetEnumsAsync(ct)));

        v1.MapPost("/metadata/yaml", async (
            HttpRequest request,
            MetadataRegistryService registry,
            CancellationToken ct) =>
        {
            try
            {
                string yamlContent;
                if (request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var body = await request.ReadFromJsonAsync<YamlMetadataRequest>(cancellationToken: ct);
                    yamlContent = body?.Yaml ?? string.Empty;
                }
                else
                {
                    using var reader = new StreamReader(request.Body);
                    yamlContent = await reader.ReadToEndAsync(ct);
                }

                var provisioned = await registry.ApplyYamlAsync(yamlContent, ct);
                return Results.Ok(new { objects = provisioned });
            }
            catch (MetadataValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        v1.MapPost("/metadata/bundle", async (
            MetadataBundle bundle,
            MetadataRegistryService registry,
            CancellationToken ct) =>
        {
            try
            {
                var provisioned = await registry.ApplyBundleAsync(bundle, ct);
                return Results.Ok(new { objects = provisioned });
            }
            catch (MetadataValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Generic Data API endpoints: /api/v1/{company}/{objectId}
        v1.MapPost("/{company}/$batch", async (
            string company,
            List<BatchOperation> operations,
            ITenantContext tenantContext,
            RecordDataService dataService,
            CancellationToken ct) =>
        {
            var companyId = ResolveCompanyId(company, tenantContext);
            return await ExecuteDataActionAsync(async () =>
            {
                var results = await dataService.BatchAsync(operations, companyId, ct);
                return Results.Ok(new { results });
            });
        });

        v1.MapGet("/{company}/{objectId}", async (
            string company,
            string objectId,
            string? filter,
            string? select,
            string? orderBy,
            int? top,
            int? skip,
            ITenantContext tenantContext,
            RecordDataService dataService,
            CancellationToken ct) =>
        {
            var companyId = ResolveCompanyId(company, tenantContext);
            return await ExecuteDataActionAsync(async () =>
            {
                var filterNode = FilterExpressionParser.Parse(filter);
                var selectFields = string.IsNullOrWhiteSpace(select)
                    ? null
                    : select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var sortFields = ParseOrderBy(orderBy);

                var query = new DataQuery(
                    Filter: filterNode,
                    OrderBy: sortFields,
                    Select: selectFields,
                    Top: top ?? 50,
                    Skip: skip ?? 0);

                var records = await dataService.QueryAsync(objectId, query, companyId, ct);
                return Results.Ok(records);
            });
        });

        v1.MapGet("/{company}/{objectId}/{id:guid}", async (
            string company,
            string objectId,
            Guid id,
            HttpResponse response,
            ITenantContext tenantContext,
            RecordDataService dataService,
            CancellationToken ct) =>
        {
            var companyId = ResolveCompanyId(company, tenantContext);
            return await ExecuteDataActionAsync(async () =>
            {
                var record = await dataService.GetAsync(objectId, id, companyId, ct);
                if (record is null)
                {
                    return Results.NotFound(new { error = $"Record '{id}' in '{objectId}' was not found." });
                }

                response.Headers.ETag = $"\"{record.RowVersion}\"";
                return Results.Ok(record);
            });
        });

        v1.MapGet("/{company}/{objectId}/{id:guid}/audit", async (
            string company,
            string objectId,
            Guid id,
            RecordDataService dataService,
            CancellationToken ct) =>
        {
            _ = company;
            return await ExecuteDataActionAsync(async () =>
            {
                var auditEntries = await dataService.GetAuditTrailAsync(objectId, id, ct);
                return Results.Ok(auditEntries);
            });
        });

        v1.MapPost("/{company}/{objectId}", async (
            string company,
            string objectId,
            Dictionary<string, object?> fields,
            HttpResponse response,
            ITenantContext tenantContext,
            RecordDataService dataService,
            CancellationToken ct) =>
        {
            var companyId = ResolveCompanyId(company, tenantContext);
            return await ExecuteDataActionAsync(async () =>
            {
                var created = await dataService.CreateAsync(objectId, fields, companyId, cancellationToken: ct);
                response.Headers.ETag = $"\"{created.RowVersion}\"";
                return Results.Created($"/api/v1/{company}/{objectId}/{created.Id}", created);
            });
        });

        v1.MapPatch("/{company}/{objectId}/{id:guid}", async (
            string company,
            string objectId,
            Guid id,
            Dictionary<string, object?> fields,
            HttpRequest request,
            HttpResponse response,
            ITenantContext tenantContext,
            RecordDataService dataService,
            CancellationToken ct) =>
        {
            if (!TryParseIfMatchRowVersion(request, out var expectedRowVersion))
            {
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
            }

            var companyId = ResolveCompanyId(company, tenantContext);
            return await ExecuteDataActionAsync(async () =>
            {
                var updated = await dataService.UpdateAsync(objectId, id, expectedRowVersion, fields, companyId, ct);
                response.Headers.ETag = $"\"{updated.RowVersion}\"";
                return Results.Ok(updated);
            });
        });

        v1.MapDelete("/{company}/{objectId}/{id:guid}", async (
            string company,
            string objectId,
            Guid id,
            HttpRequest request,
            ITenantContext tenantContext,
            RecordDataService dataService,
            CancellationToken ct) =>
        {
            if (!TryParseIfMatchRowVersion(request, out var expectedRowVersion))
            {
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
            }

            var companyId = ResolveCompanyId(company, tenantContext);
            return await ExecuteDataActionAsync(async () =>
            {
                await dataService.DeleteAsync(objectId, id, expectedRowVersion, companyId, ct);
                return Results.NoContent();
            });
        });
    }

    private static Guid? ResolveCompanyId(string companySegment, ITenantContext tenantContext)
    {
        if (Guid.TryParse(companySegment, out var parsed))
        {
            return parsed;
        }

        return tenantContext.CompanyId;
    }

    private static bool TryParseIfMatchRowVersion(HttpRequest request, out long rowVersion)
    {
        rowVersion = 0;
        var raw = request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var cleaned = raw.Trim();
        if (cleaned.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[2..].Trim();
        }
        cleaned = cleaned.Trim('"');

        return long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out rowVersion) && rowVersion > 0;
    }

    private static IReadOnlyList<SortField>? ParseOrderBy(string? orderBy)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            return null;
        }

        var list = new List<SortField>();
        foreach (var part in orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var desc = tokens.Length > 1 && string.Equals(tokens[1], "desc", StringComparison.OrdinalIgnoreCase);
            list.Add(new SortField(tokens[0], desc));
        }

        return list;
    }

    private static async Task<IResult> ExecuteDataActionAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ObjectNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (RecordNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ConcurrencyConflictException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
        }
        catch (RecordValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message, field = ex.FieldName });
        }
        catch (MetadataValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}

