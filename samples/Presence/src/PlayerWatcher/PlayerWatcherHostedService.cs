using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Presence.Grains;

namespace Presence.PlayerWatcher;

public class PlayerWatcherHostedService : IHostedService
{
    private readonly ILogger<PlayerWatcherHostedService> _logger;
    private readonly IClusterClient _client;
    private readonly IGameObserver _observer;

    private IGameGrain? _game;

    public PlayerWatcherHostedService(
        ILogger<PlayerWatcherHostedService> logger,
        IClusterClient client,
        IGameObserver observer)
    {
        _logger = logger;
        _client = client;
        _observer = observer;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // the load generator will update player ids within a range that includes this player
        var playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}");
        var player = _client.GetGrain<IPlayerGrain>(playerId);

        // poll for this player to join a game
        while (_game is null)
        {
            _logger.LogInformation("Getting current game for player {PlayerId}...",
                playerId);

            try
            {
                _game = await player.GetCurrentGameAsync();
            }
            catch (Exception error)
            {
                _logger.LogError(error,
                    "Error while requesting current game for player {PlayerId}",
                    playerId);
            }

            if (_game is null)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1_000), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        _logger.LogInformation("Observing updates for game {GameKey}", _game.GetPrimaryKey());

        // subscribe for updates
        var reference = await _client.CreateObjectReference<IGameObserver>(_observer);
        await _game.ObserveGameUpdatesAsync(reference);

        _logger.LogInformation("Subscribed successfully to game {GameKey}", _game.GetPrimaryKey());
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reference = await _client.CreateObjectReference<IGameObserver>(_observer);
            if (_game is not null && reference is not null)
            {
                await _game.UnobserveGameUpdatesAsync(reference);
            }
        }
        catch (OrleansException error)
        {
            _logger.LogWarning(error,
                "Error gracefully removing observer from the active game. Will ignore and continue to shutdown.");
        }
    }
}
