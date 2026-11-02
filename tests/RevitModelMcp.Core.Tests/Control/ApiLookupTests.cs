using System.Reflection;
using RevitModelMcp.Core.Control;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Tests.Control
{
    public sealed class ApiLookupTests
    {
        private static readonly Assembly[] Assemblies = [typeof(ApiLookup).Assembly, typeof(List<>).Assembly];

        [Test]
        [Arguments("System.DayOfWeek", "enum")]
        [Arguments("System.DateTime", "struct")]
        [Arguments("System.IDisposable", "interface")]
        public async Task TypeKind_DescribesPublicTypes(string query, string kind)
        {
            var result = Lookup(new ApiLookupRequest(query, "type"));
            await Assert.That(result.Types.Single().Kind).IsEqualTo(kind);
        }

        [Test]
        public async Task ExactType_UsesFullNameAndReportsAssemblyVersions()
        {
            var result = Lookup(new ApiLookupRequest(typeof(ApiLookup).FullName!, "type"));
            await Assert.That(result.Types.Single().FullName).IsEqualTo(typeof(ApiLookup).FullName);
            await Assert.That(result.Types.Single().Kind).IsEqualTo("class");
            await Assert.That(result.AssemblyVersions.Select(item => item.Name).ToArray())
                .IsEquivalentTo(Assemblies.Select(assembly => assembly.GetName().Name).ToArray());
            await Assert.That(result.AssemblyVersions.All(item => Version.TryParse(item.Version, out _))).IsTrue();
            await Assert.That(result.Members.Count).IsEqualTo(0);
        }

        [Test]
        public async Task SimpleName_IsCaseInsensitiveAndAmbiguityRequiresFullName()
        {
            var assembly = new FakeAssembly([typeof(Autodesk.Revit.Lookup.Sample)]);
            var simple = ApiLookup.Lookup([assembly], new ApiLookupRequest("sAmPlE", "type"), _ => null);
            await Assert.That(simple.Types.Single().FullName).IsEqualTo(typeof(Autodesk.Revit.Lookup.Sample).FullName);
            var ambiguousAssembly = new FakeAssembly([typeof(Autodesk.Revit.Lookup.Sample), typeof(Autodesk.Revit.Other.Sample)]);
            var ambiguous = ApiLookup.Lookup([ambiguousAssembly], new ApiLookupRequest("Sample"), _ => null);
            await Assert.That(ambiguous.Candidates.ToArray()).IsEquivalentTo(new[]
            {
                typeof(Autodesk.Revit.Lookup.Sample).FullName!, typeof(Autodesk.Revit.Other.Sample).FullName!
            });
            await Assert.That(ambiguous.Types.Count + ambiguous.Members.Count).IsEqualTo(0);
            var exact = ApiLookup.Lookup([ambiguousAssembly], new ApiLookupRequest(typeof(Autodesk.Revit.Lookup.Sample).FullName!, "type"), _ => null);
            await Assert.That(exact.Types.Count).IsEqualTo(1);
        }

        [Test]
        public async Task Members_DescribeGenericMethodsAndOptionalParametersWithoutInvokingCode()
        {
            var generic = Lookup(new ApiLookupRequest("System.Collections.Generic.List`1.ConvertAll", "member"));
            var method = generic.Members.Single();
            await Assert.That(method.Signature).IsEqualTo("public List<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter)");
            await Assert.That(method.ReturnType).IsEqualTo("List<TOutput>");
            var fixture = Fixture(new ApiLookupRequest("Sample", "member", 100));
            var optional = fixture.Members.Single(member => member.Name == "Convert");
            await Assert.That(optional.Signature).IsEqualTo("public static TResult Convert<TResult>(string text, int count = 1)");
            await Assert.That(optional.Parameters[1].IsOptional).IsTrue();
            await Assert.That(optional.IsStatic).IsTrue();
            await Assert.That(fixture.Members.Any(member => member.Name == "Explodes" && member.Signature == "public string Explodes { get; }")).IsTrue();
            await Assert.That(fixture.Members.Any(member => member.Name.StartsWith("get_", StringComparison.Ordinal))).IsFalse();
            await Assert.That(fixture.Members.Any(member => member.Name == "Hidden")).IsFalse();
            await Assert.That(fixture.Members.Any(member => member.MemberKind == "constructor")).IsTrue();
            await Assert.That(fixture.Members.Any(member => member.MemberKind == "event")).IsTrue();
            await Assert.That(fixture.Members.Any(member => member.MemberKind == "field")).IsTrue();
        }

        [Test]
        public async Task ObsoleteAndInherited_FiltersApplyToPublicMetadata()
        {
            var included = Fixture(new ApiLookupRequest("Sample", "member", 100, true));
            var old = included.Members.Single(member => member.Name == "Old");
            await Assert.That(old.IsObsolete).IsTrue();
            await Assert.That(old.ObsoleteMessage).IsEqualTo("Use Current.");
            await Assert.That(included.Members.Any(member => member.Name == "ToString")).IsTrue();
            var declared = Fixture(new ApiLookupRequest("Sample", "member", 100, false, false));
            await Assert.That(declared.Members.Any(member => member.Name is "Old" or "ToString")).IsFalse();
            var oldType = Fixture(new ApiLookupRequest("OldSample", "type"));
            await Assert.That(oldType.Types.Single().IsObsolete).IsTrue();
            var filteredType = Fixture(new ApiLookupRequest("OldSample", "type", IncludeObsolete: false));
            await Assert.That(filteredType.Types.Count).IsEqualTo(0);
        }

        [Test]
        public async Task Search_LimitsCombinedResultsAndMarksTruncation()
        {
            var limited = Lookup(new ApiLookupRequest("Command", Limit: 1));
            await Assert.That(limited.Types.Count + limited.Members.Count).IsEqualTo(1);
            await Assert.That(limited.Truncated).IsTrue();
            var member = Lookup(new ApiLookupRequest("RedactPaths", "member"));
            await Assert.That(member.Members.Any(item => item.Name == "RedactPaths")).IsTrue();
            var absent = Lookup(new ApiLookupRequest("NoSuchApiTypeOrMember"));
            await Assert.That(absent.Types.Count + absent.Members.Count).IsEqualTo(0);
            await Assert.That(absent.Truncated).IsFalse();
        }

        [Test]
        public async Task XmlSummaries_ArePlainFirstSentencesAndCachedPerAssembly()
        {
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(typeof(ApiLookup).Assembly.Location));
            var path = Path.Combine(Path.GetTempPath(), $"api-lookup-{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(path, """
                    <doc><members>
                    <member name="T:RevitModelMcp.Core.Control.ApiLookup"><summary>
                    Read <c>public</c> metadata. More text.
                    </summary></member>
                    <member name="M:RevitModelMcp.Core.Control.ApiLookup.Lookup(System.Collections.Generic.IReadOnlyList{System.Reflection.Assembly},RevitModelMcp.Core.Control.ApiLookupRequest,System.Func{System.Reflection.Assembly,System.String})"><summary>Look up <see cref="T:System.String"/> names. Another sentence.</summary></member>
                    <member name="T:RevitModelMcp.Core.Control.ApiLookupRequest"><summary>
                    """ + new string('x', 400) + "</summary></member></members></doc>");
                var result = ApiLookup.Lookup([assembly], new ApiLookupRequest(typeof(ApiLookup).FullName!, Limit: 100), _ => path);
                await Assert.That(result.Types.Single().Summary).IsEqualTo("Read public metadata.");
                await Assert.That(result.Members.Single(member => member.Name == "Lookup").Summary).IsEqualTo("Look up System.String names.");
                File.WriteAllText(path, "invalid XML after the first read");
                var cached = ApiLookup.Lookup([assembly], new ApiLookupRequest(typeof(ApiLookupRequest).FullName!, "type"), _ => path);
                await Assert.That(cached.Types.Single().Summary!.Length).IsEqualTo(300);
                await Assert.That(cached.SkippedCount).IsEqualTo(0);
            }
            finally { File.Delete(path); }
        }

        [Test]
        public async Task MissingXml_ReturnsNullSummaryAndOneDiagnosticPerAssembly()
        {
            var result = Lookup(new ApiLookupRequest(typeof(ApiLookup).FullName!, "type"));
            await Assert.That(result.Types.Single().Summary).IsNull();
            await Assert.That(result.SkippedCount).IsEqualTo(2);
            await Assert.That(result.Skipped[0].Reason).IsEqualTo("XML documentation not found next to RevitModelMcp.Core");
        }

        [Test]
        public async Task LoadFailure_KeepsLoadedTypesAndCountsEachMissingType()
        {
            var assembly = new FakeAssembly([typeof(ApiLookup), null, null], true);
            var result = ApiLookup.Lookup([assembly], new ApiLookupRequest(typeof(ApiLookup).FullName!, "type"), _ => null);
            await Assert.That(result.Types.Single().FullName).IsEqualTo(typeof(ApiLookup).FullName);
            await Assert.That(result.Skipped.Count(item => item.Reason == "A type could not be loaded.")).IsEqualTo(2);
            await Assert.That(result.SkippedCount).IsEqualTo(3);
        }

        private static ApiLookupResult Lookup(ApiLookupRequest request) => ApiLookup.Lookup(Assemblies, request, _ => null);

#pragma warning disable CS0618
        private static ApiLookupResult Fixture(ApiLookupRequest request) => ApiLookup.Lookup(
            [new FakeAssembly([typeof(Autodesk.Revit.Lookup.Sample), typeof(Autodesk.Revit.Lookup.OldSample)])], request, _ => null);
#pragma warning restore CS0618

        private sealed class FakeAssembly(Type?[] types, bool fail = false) : Assembly
        {
            public override AssemblyName GetName() => new("LookupFixture, Version=1.0.0.0");
            public override Type[] GetTypes() => fail
                ? throw new ReflectionTypeLoadException(types, [new TypeLoadException(), new TypeLoadException()])
                : types.OfType<Type>().ToArray();
        }
    }
}

namespace Autodesk.Revit.Lookup
{
    public class Sample
    {
        public string Explodes => throw new InvalidOperationException("Metadata lookup must not invoke getters.");
        public int Field;
        public event EventHandler Changed { add => throw new InvalidOperationException(); remove => throw new InvalidOperationException(); }
        public static TResult Convert<TResult>(string text, int count = 1) => throw new InvalidOperationException();
        [Obsolete("Use Current.")] public void Old() => throw new InvalidOperationException();
        private void Hidden() => throw new InvalidOperationException();
    }

    [Obsolete("Use Sample.")] public class OldSample;
}

namespace Autodesk.Revit.Other
{
    public class Sample;
}
