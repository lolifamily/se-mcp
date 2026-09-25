using System;
using System.Threading;

namespace Shared.Mcp;

// One lane's per-frame time budget, and whom it kills when the budget runs out.
//
// The budget belongs to the FRAME, not to a script: every script a Tick steps runs serially on the
// lane thread, so their combined time is what freezes it. When it runs out, the kill goes to exactly
// one script — the one on the lane's stack right then — by writing its id into the lane's KillId.
// The Bail calls and catch filters injected into each script compare KillId against the id baked
// into THAT script at compile time (Compiler.InjectTimeoutChecks), so nothing else answers to it.
//
// An id rather than a lane-wide flag, because instrumented code OUTLIVES its script: a Harmony
// patch, an event handler or a thread a script started keeps running after the script that
// compiled it is gone, and a flag would kill that code whenever any later script on the lane went
// over budget. The id travels with the code and can only ever match the script it came from.
// ExitStep clears it the moment that script leaves the stack and nothing raises it again, so from
// then on the code it left behind runs unguarded for good.
//
// Every write that raises a kill happens under `gate`, which the step boundaries take too: the id
// raised is the one on the stack at that instant, and that step cannot leave the stack before the
// write lands. SE never re-enters a lane's pump, so frames don't nest and neither do steps.
internal sealed class FrameWatchdog(Action<int> setKillId, int budgetMs)
{
    private readonly object gate = new();

    // Frame generation, bumped by BeginFrame and EndFrame alike. The lane thread is the sole writer,
    // so `++` needs no atomic. Only ever compared for equality, so wrap is a non-event.
    private volatile int gen;

    // The generation whose budget ran out, or -1 (never a live generation). Compared for equality
    // against `gen`: a timer body that fires for an older frame writes that older number, which
    // reads as "not tripped" all by itself — a stale timer can never trip a later frame.
    private volatile int deadGen = -1;

    // Which script is on the lane's stack right now, or 0. Only touched under `gate`.
    private int stepScript;

    // Lane thread only.
    private Timer timer;

    // Whether this frame's budget is already spent. Lane thread only — `gen` has no other writer,
    // so the two reads are coherent without the lock.
    public bool Tripped => deadGen == gen;

    // Lane pump, start of a frame that has scripts to step: arm a full budget.
    public void BeginFrame()
    {
        var armed = ++gen;
        timer?.Dispose();
        setKillId(0);
        timer = new Timer(_ => Expire(armed), null, budgetMs, Timeout.Infinite);
    }

    // Lane pump, end of that frame — pair with BeginFrame in a finally. Bumping `gen` un-trips the
    // frame and retires a timer body still in flight, so nothing is left standing while the lane
    // idles.
    public void EndFrame()
    {
        gen++;
        timer?.Dispose();
        timer = null;
        setKillId(0);
    }

    // Around one script's step: publish who is on the stack, so the timer knows whom to kill. A step
    // registering after the budget already ran out — the timer fired between the pump's Tripped
    // check and here — arms its own kill, and is over the limit from its first check.
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

    // Timer thread. A generation that no longer matches means that frame already ended — nothing to
    // do. Otherwise the victim is whoever is on the stack, or 0 between steps: the frame is then
    // spent with nobody to kill, and the pump ends the scripts still waiting for their turn.
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
