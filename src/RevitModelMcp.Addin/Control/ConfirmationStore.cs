using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class ConfirmationStore
{
    internal static DocumentConfirmationTokens Tokens { get; } = new();
}
