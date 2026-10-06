using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Sleepyshark.Officina;

/// <summary>JSON for application types: typed functions' input and output (TOOL-01), and typed output (OUT-01).</summary>
internal static class TypedJson
{
    /// <summary>
    /// How typed functions read their input and write their output, and how typed output is read (OUT-01): members in camel case,
    /// numbers never read from strings, nullable annotations and required members respected, unknown properties refused, enums by name.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    internal static readonly JsonSchemaExporterOptions Exporter = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (context, node) =>
        {
            var provider = context.PropertyInfo?.AttributeProvider ?? (context.PropertyInfo is null ? context.TypeInfo.Type : null);
            Describe(node, provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false).OfType<DescriptionAttribute>().FirstOrDefault());
            return node;
        },
    };

    /// <summary>Puts <paramref name="description"/>, if any, first in <paramref name="node"/>'s schema.</summary>
    internal static void Describe(JsonNode? node, DescriptionAttribute? description)
    {
        if (node is JsonObject schema && description is not null)
        {
            schema.Insert(0, "description", description.Description);
        }
    }
}
