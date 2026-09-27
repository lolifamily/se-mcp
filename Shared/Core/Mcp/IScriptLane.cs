namespace Shared.Mcp;

// What ExecuteCodeTool needs of a lane: whether it takes work yet, and taking it. Executor steps
// its scripts from a game pump (main, render); ParallelExecutor runs each on a thread of its own.
public interface IScriptLane
{
    bool Initialized { get; }

    void Enqueue(WorkItem item);
}
