using KidTime.Domain.Contracts;

namespace KidTime.SessionAgent;

/// <summary>
/// How a shortcut on the child's desktop reaches the tray agent that is already running.
///
/// The agent cannot simply be started a second time. The service launches exactly one copy and
/// supervises it, and <see cref="NamedPipeHost"/> in the service accepts a connection only from
/// that copy's process id - so a second agent would hold the single-instance mutex, be refused by
/// the pipe, and leave the supervised one unable to start at all. A shortcut therefore never
/// becomes the agent: started with <see cref="ShowArgument"/> it signals the running one and
/// exits, which is the same request the tray icon makes when it is clicked.
///
/// The signal carries nothing. It cannot pass a rule, a command, or a value of any kind - the
/// whole vocabulary is "open the window", which the child can already do from the tray. Widening
/// it would be widening the way the unelevated session talks to the enforcement boundary, and
/// that boundary is the named pipe, not this.
/// </summary>
internal static class AgentActivation
{
    /// <summary>
    /// What the shortcuts the service writes pass on the command line. It is
    /// <see cref="SessionAgentArguments.Show"/> because both halves must agree and neither
    /// project references the other.
    /// </summary>
    public const string ShowArgument = SessionAgentArguments.Show;

    /// <summary>
    /// <c>Local\</c> scopes the name to the Windows session, so this reaches the agent in the
    /// caller's own session and nothing else: another signed-in user's agent is unreachable, and
    /// so is the service's.
    /// </summary>
    private const string EventName = @"Local\KidTime.SessionAgent.Show.v1";

    /// <summary>
    /// How long a shortcut waits for an agent to answer. The service relaunches the agent every
    /// two seconds, so the usual reason for nobody being there is that the child clicked during a
    /// restart; waiting a few seconds turns that into an opened window rather than into nothing
    /// happening. Beyond it there is genuinely no agent, and the child is better served by the
    /// click ending than by a process that waits forever.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    public static bool IsShowRequest(IReadOnlyList<string> arguments) =>
        SessionAgentArguments.IsShowRequest(arguments);

    /// <summary>
    /// Asks the running agent to open its screen-time window. False when none answered, which is
    /// reported rather than shown: this process has no interface of its own, and putting one up
    /// would mean a second window telling the child about the window they cannot have.
    /// </summary>
    public static bool RequestShow()
    {
        var deadline = DateTime.UtcNow + StartupGrace;
        while (true)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(EventName);
                handle.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(RetryInterval);
            }
            catch (UnauthorizedAccessException)
            {
                // Someone else's agent owns a name in this session. Not ours to signal.
                return false;
            }
        }
    }

    /// <summary>
    /// Created by the agent that owns the session, and waited on for as long as it runs. Auto-reset
    /// because each click is one request: two clicks while the window is opening must not queue a
    /// second open behind the first.
    /// </summary>
    public static EventWaitHandle CreateListener() =>
        new(initialState: false, EventResetMode.AutoReset, EventName);
}
