namespace Wendmem.Experiences;

public record TrajectoryPartition(
    IReadOnlyList<Trajectory> Successful,
    IReadOnlyList<Trajectory> Failed,
    IReadOnlyList<TrajectoryPair> ComparativePairs);

public record TrajectoryPair(
    string TaskQuery,
    Trajectory Higher,
    Trajectory Lower);

public static class TrajectoryPreprocessor
{
    public static TrajectoryPartition Partition(
        IReadOnlyList<Trajectory> trajectories,
        float successThreshold)
    {
        var successful = trajectories.Where(t => t.Score >= successThreshold).ToList();
        var failed = trajectories.Where(t => t.Score < successThreshold).ToList();

        var pairs = trajectories
            .GroupBy(t => t.TaskQuery)
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                var sorted = g.OrderByDescending(t => t.Score).ToList();
                var hi = sorted.First();
                var lo = sorted.Last();
                return hi.Score > lo.Score
                    ? new TrajectoryPair(g.Key, hi, lo)
                    : null;
            })
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        return new TrajectoryPartition(successful, failed, pairs);
    }
}
