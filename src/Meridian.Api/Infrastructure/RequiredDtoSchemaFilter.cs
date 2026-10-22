using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Meridian.Api.Infrastructure;

/// <summary>
/// Response DTOs always carry every property (null when empty), so mark them all required.
/// The generated TypeScript client then has exact, non-optional types.
/// </summary>
public sealed class RequiredDtoSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var t = context.Type;
        var isDto = t.Name.EndsWith("Dto") || (t.IsGenericType && t.Name.StartsWith("PagedResult"));
        if (!isDto || schema.Properties is null) return;
        foreach (var name in schema.Properties.Keys) schema.Required.Add(name);
    }
}
