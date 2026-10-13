using System;
using System.Threading;

namespace Shared.Mcp;

// A frame lane's per-frame time budget, and whom it kills when the budget runs out.
//
// The budget belongs to the frame, not to a script: every script a Tick steps runs serially on the
// lane thread, so their combined time is what freezes it. When it runs out, exactly one script is
// killed: the one on the lane's stack right then, by writing its id into the lane's KillId. Each
// script's injected checks compare KillId with the id baked into that script (Compiler.InjectGuards)
// and answer only on the lane thread (ScriptGuard's Killing), so nothing else answers to it.
//
// An id rather than a lane-wide flag, because a script's code outlives it: a Harmony patch, an event
// handler or a thread it started keeps running after the script is gone, and a flag would kill that
// code whenever a later script on the lane went over budget. ExitStep clears the id the moment the
// script leaves the stack, and nothing raises it again, so code it left behind runs unguarded from
// then on.
//
// Every write that raises a kill happens under `gate`, which the step boundaries take too: the id
// raised is the one on the stack at that instant, and that step can't leave the stack before the
// write lands. SE never re-enters a lane's pump, so frames don't nest and neither do steps.
internal sealed class FrameWatchdog(Action<int> setKillId, int budgetMs)
{
    private readonly object gate = new();

    // Frame generation, bumped by BeginFrame and EndFrame, on the lane thread only. Compared only for
    // equality, so wrapping is a non-event.
    private volatile int gen;

    // The generation whose budget ran out, or -1, never a live one. A timer firing for an older frame
    // writes that older number, which reads as "not tripped" by itself: a stale timer can't trip a
    // later frame.
    private volatile int deadGen = -1;

    // The script on the lane's stack right now, or 0. Under `gate`.
    private int stepScript;

    // Lane thread only.
    private Timer timer;

    // Whether this frame's budget is spent. Lane thread only: `gen` has no other writer, so the two
    // reads agree without the lock.
    public bool Tripped => deadGen == gen;

    // Start of a frame with work in it: arms a full budget.
    public void BeginFrame()
    {
        var armed = Interlocked.Increment(ref gen);
        timer?.Dispose();
        setKillId(0);
        timer = new Timer(_ => Expire(armed), null, budgetMs, Timeout.Infinite);
    }

    // End of that frame; pair with BeginFrame in a finally. Bumping `gen` un-trips the frame and
    // retires a timer callback still in flight, so nothing stays armed while the lane idles.
    public void EndFrame()
    {
        Interlocked.Increment(ref gen);
        timer?.Dispose();
        timer = null;
        setKillId(0);
    }

    // Around each run of a script's code, a step or its cleanup: publishes who is on the stack, so the
    // timer knows whom to kill. One entering after the budget already ran out (the timer fired between
    // the pump's Tripped check and here) raises its own kill, and is over the limit from its first
    // check.
    public void EnterStep(int scriptId)
    {
        lock (gate)
        {
            stepScript = scriptId;
            if (deadGen == gen) setKillId(scriptId);
        }
    }

    // Pair with EnterStep in a finally. Clearing the kill here is what releases the code the script
    // left behind: left raised, every Bail that code passes would throw from then on.
    public void ExitStep()
    {
        lock (gate)
        {
            stepScript = 0;
            setKillId(0);
        }
    }

    // Timer thread. A generation that no longer matches means that frame already ended. Otherwise the
    // victim is whoever is on the stack, or 0 between steps: the frame is then spent with nobody to
    // kill, and the pump ends the scripts still waiting for their turn.
    private void Expire(int armed)
    {
        lock (gate)
        {
            var victim = stepScript;
            if (armed != gen) return;
            setKillId(victim);
            deadGen = armed;
        }
    }
}
