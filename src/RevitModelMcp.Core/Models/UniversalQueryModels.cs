using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

public enum ParameterOperator
{
    Equals,
    Contains,
    Greater,
    Less,
    Empty,
    NotEmpty,
    Exists
}

public sealed class ParameterFilterSpec
{
    public string Parameter { get; set; } = string.Empty;

    public ParameterOperator Operator { get; set; }

    public string? Value { get; set; }
}

public sealed class ElementFilterSpec
{
    public IReadOnlyList<string> Categories { get; set; } = Array.Empty<string>();

    public string? Family { get; set; }

    public string? Type { get; set; }

    public string? Level { get; set; }

    public string? View { get; set; }

    public string? Workset { get; set; }

    public string? Phase { get; set; }

    public string? AreaScheme { get; set; }

    public IReadOnlyList<ParameterFilterSpec> Parameters { get; set; } = Array.Empty<ParameterFilterSpec>();
}

public sealed class QuerySortSpec
{
    public string Field { get; set; } = "id";

    public bool Descending { get; set; }
}

[DataContract]
public sealed class QueryElementsData
{
    [DataMember(Name = "offset")]
    public int Offset { get; set; }

    [DataMember(Name = "limit")]
    public int Limit { get; set; }

    [DataMember(Name = "total")]
    public int Total { get; set; }

    [DataMember(Name = "hasMore")]
    public bool HasMore { get; set; }

    [DataMember(Name = "fields")]
    public List<string> Fields { get; set; } = new();

    [DataMember(Name = "elements")]
    public List<QueryElementItem> Elements { get; set; } = new();
}

[DataContract]
public sealed class QueryElementItem : ElementGeometryData
{
    [DataMember(Name = "id")]
    public long Id { get; set; }

    [DataMember(Name = "values")]
    public Dictionary<string, QueryFieldValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[DataContract]
public sealed class QueryFieldValue
{
    [DataMember(Name = "hasValue")]
    public bool HasValue { get; set; }

    [DataMember(Name = "value", EmitDefaultValue = false)]
    public string? Value { get; set; }

    [DataMember(Name = "numericValue", EmitDefaultValue = false)]
    public double? NumericValue { get; set; }

    [DataMember(Name = "unit", EmitDefaultValue = false)]
    public string? Unit { get; set; }

    [DataMember(Name = "source", EmitDefaultValue = false)]
    public string? Source { get; set; }
}

[DataContract]
public sealed class AggregateElementsData
{
    [DataMember(Name = "matchedElements")]
    public int MatchedElements { get; set; }

    [DataMember(Name = "groupBy")]
    public List<string> GroupBy { get; set; } = new();

    [DataMember(Name = "numericField", EmitDefaultValue = false)]
    public string? NumericField { get; set; }

    [DataMember(Name = "numericFieldFound", EmitDefaultValue = false)]
    public bool? NumericFieldFound { get; set; }

    [DataMember(Name = "groups")]
    public List<AggregateGroup> Groups { get; set; } = new();
}

[DataContract]
public sealed class AggregateGroup
{
    [DataMember(Name = "keys")]
    public Dictionary<string, string?> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [DataMember(Name = "count")]
    public int Count { get; set; }

    [DataMember(Name = "numericCount")]
    public int NumericCount { get; set; }

    [DataMember(Name = "sum", EmitDefaultValue = false)]
    public double? Sum { get; set; }

    [DataMember(Name = "average", EmitDefaultValue = false)]
    public double? Average { get; set; }

    [DataMember(Name = "unit", EmitDefaultValue = false)]
    public string? Unit { get; set; }
}

public sealed class PreparedQueryRecord
{
    public long Id { get; set; }

    public Dictionary<string, PreparedQueryValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class PreparedQueryValue
{
    public string? Text { get; set; }

    public double? Number { get; set; }

    public string? Unit { get; set; }
}
