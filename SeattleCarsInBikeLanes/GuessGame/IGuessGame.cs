using static SeattleCarsInBikeLanes.Models.GuessGame.GuessGameState;

namespace SeattleCarsInBikeLanes.GuessGame
{
    public interface IGuessGame
    {
        Task StartedRound(RoundInfo roundInfo);

        Task ReceiveImage(ImageInfo imageInfo);

        Task JoinedGame(PlayerScoreInfo player);

        Task LeftGame(string username);

        Task AdminLeftGame();

        Task ReceiveCountdown(string type, int number);
    }
}
