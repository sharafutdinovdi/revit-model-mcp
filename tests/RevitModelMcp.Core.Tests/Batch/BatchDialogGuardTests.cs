using System.Text.RegularExpressions;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Core.Tests.Batch;

public sealed class BatchDialogGuardTests
{
    [Test]
    public async Task Run_ReportsThrowingHandlerWithoutApplyingAnswer()
    {
        var failure = new InvalidOperationException("Handler failed.");
        Exception? reported = null;
        var answered = false;
        Action handler = () => throw failure;
        BatchDialogGuard.Run(() =>
        {
            handler();
            answered = true;
        }, exception => reported = exception);
        await Assert.That(reported).IsEqualTo(failure);
        await Assert.That(answered).IsFalse();
    }

    [Test]
    public async Task Run_ContainsCatastrophicRegexTimeoutAndLeavesAnswerUnchanged()
    {
        Exception? reported = null;
        var answered = false;
        var expression = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(20));
        BatchDialogGuard.Run(() =>
        {
            _ = expression.IsMatch(new string('a', 10000) + "!");
            answered = true;
        }, exception => reported = exception);
        await Assert.That(reported is RegexMatchTimeoutException).IsTrue();
        await Assert.That(answered).IsFalse();
    }

    [Test]
    public void Run_ContainsReportingFailure()
    {
        BatchDialogGuard.Run(() => throw new InvalidOperationException(),
            _ => throw new IOException());
    }

    [Test]
    public async Task Run_CompletesSuccessfulHandlerWithoutReporting()
    {
        var answered = false;
        var reported = false;
        BatchDialogGuard.Run(() => answered = true, _ => reported = true);
        await Assert.That(answered).IsTrue();
        await Assert.That(reported).IsFalse();
    }

    [Test]
    public async Task RedactPaths_RemovesDirectoriesAndPreservesDialogText()
    {
        var redacted = BatchDialogGuard.RedactPaths(@"Open C:\Models\Example.rvt or \\share\Models\Linked.rvt?");
        await Assert.That(redacted).IsEqualTo("Open Example.rvt or Linked.rvt?");
    }
}
