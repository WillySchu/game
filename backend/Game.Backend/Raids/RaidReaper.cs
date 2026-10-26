using Microsoft.Extensions.Options;

namespace RaidZero.Backend.Raids;

/// <summary>
/// Every few seconds, aborts raids whose game server stopped heartbeating and refunds the players inside.
/// One failed pass is logged and the next one tries again, so a transient database error can't stop it.
/// </summary>
public sealed class RaidReaper(RaidService raids, IOptions<BackendOptions> options, ILogger<RaidReaper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Raids;
        if (!settings.ReaperEnabled)
        {
            log.LogWarning("The raid reaper is off: raids whose server crashes will never be refunded.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, settings.ReaperIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await raids.ReapAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Raid reaper pass failed; retrying next tick.");
            }
        }
    }
}
