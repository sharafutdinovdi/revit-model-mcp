namespace RevitModelMcp.Core.Control;

public static class TaggingPolicy
{
    public static bool CanTemporarilyIsolate(int independentTargetCount, bool includesRooms,
        bool supportsTemporaryVisibility, bool hasActiveTemporaryMode) =>
        independentTargetCount > 0 && !includesRooms && supportsTemporaryVisibility && !hasActiveTemporaryMode;
}
