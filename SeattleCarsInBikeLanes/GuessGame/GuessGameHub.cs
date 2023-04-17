using Microsoft.AspNetCore.SignalR;
using static SeattleCarsInBikeLanes.Models.GuessGame.GuessGameState;

namespace SeattleCarsInBikeLanes.GuessGame
{
    public class GuessGameHub : Hub<IGuessGame>
    {
        private readonly ILogger<GuessGameHub> logger;
        private readonly GuessGameManager gameManager;

        public GuessGameHub(ILogger<GuessGameHub> logger,
            GuessGameManager gameManager)
        {
            this.logger = logger;
            this.gameManager = gameManager;
        }

        public async Task AddToGame(string gameCode, string username)
        {
            bool addedUser = gameManager.AddUser(gameCode, Context.ConnectionId, username);
            if (addedUser)
            {
                logger.LogInformation($"Added {username} with connection id of {Context.ConnectionId} to {gameCode}");
                await Groups.AddToGroupAsync(Context.ConnectionId, gameCode);
                await Clients.Group(gameCode).JoinedGame(new PlayerScoreInfo() { Username = username, Score = 0 });
            }
        }

        public IList<PlayerScoreInfo> GetPlayers(string gameCode)
        {
            return gameManager.GetPlayersWithScores(gameCode);
        }

        public RoundInfo? GetRoundInfo(string gameCode)
        {
            return gameManager.GetRoundInfo(gameCode);
        }

        public async Task StartGame(string gameCode)
        {
            await gameManager.StartGame(gameCode, Context.ConnectionId);
        }

        public void StartCountdown(string gameCode, string type, int seconds)
        {
            gameManager.StartCountdown(gameCode, Context.ConnectionId, type, seconds);
        }

        public async Task StartRound(string gameCode)
        {
            await gameManager.StartRound(gameCode, Context.ConnectionId);
        }

        public ImageInfo? GetRoundImage(string gameCode)
        {
            return gameManager.GetRoundImage(gameCode);
        }

        public void Guess(string gameCode, double latitude, double longitude)
        {
            gameManager.Guess(gameCode, Context.ConnectionId, latitude, longitude);
        }

        public async Task LockIn(string gameCode)
        {
            await gameManager.LockIn(gameCode, Context.ConnectionId);
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            logger.LogInformation($"Disconnecting {Context.ConnectionId}");
            gameManager.RemoveUser(Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }
    }
}
