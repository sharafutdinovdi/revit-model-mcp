using System.Runtime.Serialization;

namespace RevitModelMcp.Core.Models;

[DataContract]
public sealed record SkippedRead
{
    [DataMember(Name = "what")] public string What { get; init; } = string.Empty;
    [DataMember(Name = "reason")] public string Reason { get; init; } = string.Empty;
}

public sealed class SkippedReadDiagnostics
{
    public const int Limit = 100;

    private static readonly AsyncLocal<SkippedReadDiagnostics?> Active = new();

    public static SkippedReadDiagnostics? Current
    {
        get => Active.Value;
        set => Active.Value = value;
    }

    public List<SkippedRead> Items { get; } = [];

    public int Count { get; private set; }

    public void Add(string what, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(what)) throw new ArgumentException("A field description is required.", nameof(what));
        if (exception is null) throw new ArgumentNullException(nameof(exception));
        Count++;
        if (Items.Count < Limit)
        {
            Items.Add(new SkippedRead { What = what, Reason = exception.Message });
        }
    }
}
