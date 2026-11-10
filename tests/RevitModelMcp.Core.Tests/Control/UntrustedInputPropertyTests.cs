using System.Diagnostics;
using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;
using RevitModelMcp.Core.Policy;
using RevitModelMcp.Core.Serialization;

namespace RevitModelMcp.Core.Tests.Control;

public sealed class UntrustedInputPropertyTests
{
    private static readonly Config PropertyConfig = Config.QuickThrowOnFailure
        .WithMaxTest(100)
        .WithEndSize(100)
        .WithReplay(FSharpOption<Replay>.Some(new Replay(new Rnd(20261110UL), null)));

    [Test]
    public void ArbitraryJobTextReturnsAResult() => CheckProperty(
        Prop.ForAll<string>(content => HasConsistentRefusal(ControlJobParser.Parse(content))));

    [Test]
    public void MalformedJobsAreRefused() => CheckProperty(
        Prop.ForAll<string>(content =>
        {
            var json = "{\"command\":\"move\",\"elementIds\":[1],\"dxMm\":" + JsonSerializer.Serialize(content) + ",}";
            var result = ControlJobParser.Parse(json);
            return result.Kind == ControlJobKind.Invalid && !string.IsNullOrWhiteSpace(result.Error);
        }));

    [Test]
    public void ArbitraryJsonValuesDoNotEscapeTheParser() => CheckProperty(
        Prop.ForAll<string, int>((text, number) =>
        {
            object?[] values = [null, text, number, true, new[] { text }, new
            {
                value = text
            }, new[] { number }];
            string[] commands = ["move", "copy", "execute-code", "create-wall", "view-elements", "query-elements", "set-read-only", "batch"];
            string[] fields = ["command", "elementIds", "dxMm", "count", "code", "offset", "limit", "enabled", "steps", "categories"];
            foreach (var command in commands)
            foreach (var field in fields)
            {
                var job = new Dictionary<string, object?>
                {
                    ["command"] = command
                };
                job[field] = values[(int)((uint)number % (uint)values.Length)];
                if (!HasConsistentRefusal(ControlJobParser.Parse(JsonSerializer.Serialize(job)))) return false;
            }
            return true;
        }));

    [Test]
    public void ValidReadJobsRoundTrip() => CheckProperty(
        Prop.ForAll<NonEmptyString, NonNegativeInt, PositiveInt>((view, offset, limit) =>
        {
            var job = new ControlJobContract
            {
                Command = "view-elements",
                View = "view:" + view.Get,
                Offset = offset.Get,
                Limit = limit.Get,
                CorrelationId = view.Get
            };
            var result = ParseContract(job);
            return result.Kind == ControlJobKind.ViewElements && result.View == job.View!.Trim()
                && result.Offset == offset.Get && result.Limit == Math.Min(limit.Get, 5000)
                && result.CorrelationId == job.CorrelationId;
        }));

    [Test]
    public void ValidActionJobsRoundTrip() => CheckProperty(
        Prop.ForAll<PositiveInt, int, bool>((elementId, distance, dryRun) =>
        {
            var job = new ControlJobContract
            {
                Command = "move",
                ElementIds = [elementId.Get],
                DxMm = distance / 10.0,
                DyMm = -distance / 10.0,
                DzMm = 0,
                DryRun = dryRun
            };
            var result = ParseContract(job);
            return result.Kind == ControlJobKind.Action && result.Action is { } action
                && action.ElementIds.SequenceEqual(job.ElementIds) && action.DxMm == job.DxMm
                && action.DyMm == job.DyMm && action.DryRun == dryRun;
        }));

    [Test]
    public void PagingBoundsHoldForAllIntegers() => CheckProperty(
        Prop.ForAll<int, int>((offset, limit) =>
        {
            foreach (var candidateOffset in new[] { offset, int.MinValue, -1, 0, int.MaxValue })
            foreach (var candidateLimit in new[] { limit, int.MinValue, 0, 1, 5000, 5001, int.MaxValue })
            foreach (var command in new[] { "view-elements", "list-sheets", "schedule-data" })
            {
                var result = ParseContract(new ControlJobContract
                {
                    Command = command,
                    View = "view",
                    Offset = candidateOffset,
                    Limit = candidateLimit
                });
                var valid = candidateOffset >= 0 && candidateLimit > 0 && (command == "view-elements" || candidateLimit <= 5000);
                if ((result.Kind != ControlJobKind.Invalid) != valid) return false;
                if (valid && (result.Offset != candidateOffset || result.Limit != Math.Min(candidateLimit, 5000))) return false;
            }
            return true;
        }));

    [Test]
    public void CopyCountBoundsHoldForAllIntegers() => CheckProperty(
        Prop.ForAll<int>(count => new[] { count, int.MinValue, 0, 1, 100, 101, int.MaxValue }.All(candidate =>
        {
            var result = ActionJobParser.Parse("copy", new ControlJobContract
            {
                ElementIds = [1],
                DxMm = 1,
                DyMm = 0,
                Count = candidate
            });
            return (result.Kind == ControlJobKind.Action) == (candidate is >= 1 and <= 100);
        })));

    [Test]
    public void MoveOffsetsMustBeFinite() => CheckProperty(
        Prop.ForAll<double>(distance => new[] { distance, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue }.All(candidate =>
        {
            var result = ActionJobParser.Parse("move", new ControlJobContract
            {
                ElementIds = [1],
                DxMm = candidate,
                DyMm = 0
            });
            return (result.Kind == ControlJobKind.Action) == double.IsFinite(candidate);
        })));

    [Test]
    public void CodeSourcePreservesSubmittedText() => CheckProperty(
        Prop.ForAll<NonNull<string>>(code =>
        {
            var source = CodeSource.BuildSource(code.Get);
            return source.Contains("#line 1 \"submitted.cs\"\n" + code.Get, StringComparison.Ordinal)
                && (CodeSource.IsCompilationUnit(code.Get) ? source.EndsWith(code.Get, StringComparison.Ordinal)
                    : source.EndsWith(code.Get + "\n#line default\n} }", StringComparison.Ordinal));
        }));

    [Test]
    public void CodeHashesMatchUtf8Sha256() => CheckProperty(
        Prop.ForAll<NonNull<string>>(code =>
        {
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(code.Get))).ToLowerInvariant();
            return CodeSource.CodeHash(code.Get) == expected
                && CodeSource.CacheKey(code.Get, "auto") == CodeSource.CodeHash("auto\0" + code.Get)
                && CodeSource.CacheKey(code.Get, "auto") != CodeSource.CacheKey(code.Get, "group");
        }));

    [Test]
    public void RedactedPathsContainOnlyTheFileName() => CheckProperty(
        Prop.ForAll<NonNull<string>, NonNull<string>>((directory, file) =>
        {
            var directoryName = Convert.ToHexString(Encoding.UTF8.GetBytes(directory.Get)) + "folder";
            var fileName = Convert.ToHexString(Encoding.UTF8.GetBytes(file.Get)) + ".rvt";
            foreach (var path in new[] { $"C:\\{directoryName}\\nested\\{fileName}", $"C:/{directoryName}/nested/{fileName}",
                $"\\\\server\\{directoryName}\\{fileName}", $"RSN://server/{directoryName}/{fileName}" })
            {
                var json = JsonSerializer.Serialize(new
                {
                    path
                });
                var redacted = CommandResponseJsonSerializer.RedactPaths(json);
                using var document = JsonDocument.Parse(redacted);
                if (document.RootElement.GetProperty("path").GetString() != fileName
                    || CommandResponseJsonSerializer.RedactPaths(redacted) != redacted) return false;
            }
            return true;
        }));

    [Test]
    public void ResponseSerializationIsCultureInvariant() => CheckProperty(
        Prop.ForAll<int, decimal>((count, value) =>
        {
            var previousCulture = CultureInfo.CurrentCulture;
            var previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                string? expected = null;
                foreach (var culture in new[] { "en-US", "fr-FR", "ar-SA", "tr-TR" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                    var response = CommandResponse<decimal[]>.Ok("query-elements", [value, count], 0);
                    var json = CommandResponseJsonSerializer.Serialize(response);
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.GetProperty("data")[0].GetDecimal() != value) return false;
                    expected ??= json;
                    if (json != expected) return false;
                }
                return true;
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        }));

    [Test]
    public void CodeResultNumbersRoundTripInEveryCulture() => CheckProperty(
        Prop.ForAll<double>(value =>
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "fr-FR", "ar-SA" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    using var json = JsonDocument.Parse(CodeResultLimiter.ToJson(value));
                    if (double.IsFinite(value) ? json.RootElement.GetDouble() != value : json.RootElement.ValueKind != JsonValueKind.Null) return false;
                }
                return true;
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
        }));

    [Test]
    public void ArbitraryPolicyJsonFailsClosedWhenUnreadable() => CheckProperty(
        Prop.ForAll<string>(text =>
        {
            var policy = MachinePolicy.ParseJson(text);
            return !policy.Unreadable || policy.IsReadOnly && policy.CodeExecutionOff
                && policy.HttpEnabled == false && policy.UpdateMode == "off" && !string.IsNullOrWhiteSpace(policy.Problem);
        }));

    [Test]
    public void PolicyFlagsRoundTrip() => CheckProperty(
        Prop.ForAll<bool, bool, bool>((readOnly, redactPaths, httpEnabled) =>
        {
            var policy = MachinePolicy.ParseJson(JsonSerializer.Serialize(new
            {
                readOnly,
                redactPaths,
                httpEnabled
            }));
            return !policy.Unreadable && policy.ReadOnly == readOnly && policy.RedactPaths == redactPaths && policy.HttpEnabled == httpEnabled;
        }));

    private static bool HasConsistentRefusal(ControlJobParseResult result) =>
        result.Kind == ControlJobKind.Invalid ? !string.IsNullOrWhiteSpace(result.Error) : result.Error is null;

    private static ControlJobParseResult ParseContract(ControlJobContract job)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(ControlJobContract),
            new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            }).WriteObject(stream, job);
        return ControlJobParser.Parse(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void CheckProperty(Property property)
    {
        var stopwatch = Stopwatch.StartNew();
        try { Check.One(PropertyConfig, property); }
        finally { Console.WriteLine($"Property cases=100; replay=20261110; elapsed={stopwatch.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}ms"); }
    }
}
