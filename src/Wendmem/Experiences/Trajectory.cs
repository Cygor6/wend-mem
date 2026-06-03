namespace Wendmem.Experiences;

public record TrajectoryMessage(string Role, string Content);

public record Trajectory(
    string TaskQuery,
    IReadOnlyList<TrajectoryMessage> Messages,
    float Score,
    IReadOnlyList<string>? ToolsUsed = null);
