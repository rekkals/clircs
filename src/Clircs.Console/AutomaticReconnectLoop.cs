using Clircs.Networking;

namespace Clircs.ConsoleClient;

internal enum AutomaticReconnectOutcome
{
    Connected,
    Canceled,
    Exhausted,
    NoAttemptsConfigured
}

internal sealed class AutomaticReconnectLoop
{
    private static readonly double[] DelayScheduleSeconds = [2d, 5d, 10d, 20d, 30d, 60d];
    private readonly ReconnectPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Func<int, CancellationToken, Task> _attempt;
    private readonly Func<double> _random;

    public AutomaticReconnectLoop(
        ReconnectPolicy policy,
        Func<int, CancellationToken, Task> attempt,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        Func<double>? random = null)
    {
        _policy = policy.Validate();
        _attempt = attempt;
        _wait = wait ?? Task.Delay;
        _random = random ?? Random.Shared.NextDouble;
    }

    public async Task<AutomaticReconnectOutcome> RunAsync(
        Action<int, int, TimeSpan> attemptScheduled,
        Action<int, Exception> attemptFailed,
        CancellationToken cancellationToken)
    {
        if (_policy.MaximumAttempts == 0)
        {
            return AutomaticReconnectOutcome.NoAttemptsConfigured;
        }

        for (var attempt = 1; attempt <= _policy.MaximumAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return AutomaticReconnectOutcome.Canceled;
            }

            var delay = DelayForAttempt(attempt, _policy, _random());
            attemptScheduled(attempt, _policy.MaximumAttempts, delay);
            try
            {
                await _wait(delay, cancellationToken).ConfigureAwait(false);
                await _attempt(attempt, cancellationToken).ConfigureAwait(false);
                return AutomaticReconnectOutcome.Connected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return AutomaticReconnectOutcome.Canceled;
            }
            catch (Exception exception)
            {
                attemptFailed(attempt, exception);
            }
        }

        return AutomaticReconnectOutcome.Exhausted;
    }

    internal static TimeSpan DelayForAttempt(int attempt, ReconnectPolicy policy, double random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(random, 0d);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(random, 1d);
        var scheduled = attempt <= DelayScheduleSeconds.Length ? DelayScheduleSeconds[attempt - 1] : 60d;
        var seconds = Math.Clamp(scheduled, policy.InitialDelay.TotalSeconds, policy.MaximumDelay.TotalSeconds);
        var jitter = random * Math.Min(1.5d, seconds * 0.08d);
        return TimeSpan.FromSeconds(seconds + jitter);
    }
}
