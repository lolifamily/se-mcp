namespace Shared.Mcp;

// Where scripts run. Executor steps its scripts from a game pump (main, render); ParallelExecutor runs
// each on a thread of its own. ExecuteCodeTool needs only these two things of a lane.
public interface IScriptLane
{
    // Whether the lane takes work yet: false until the host's pump has set it up, and ExecuteCodeTool
    // turns requests away until then.
    bool Initialized { get; }

    // Takes the request; the lane answers it, however the script ends.
    void Enqueue(WorkItem item);
}
