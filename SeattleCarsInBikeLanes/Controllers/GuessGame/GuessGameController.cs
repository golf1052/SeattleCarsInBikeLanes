using Microsoft.AspNetCore.Mvc;
using SeattleCarsInBikeLanes.GuessGame;
using SeattleCarsInBikeLanes.Models.GuessGame;

namespace SeattleCarsInBikeLanes.Controllers.GuessGame
{
    [Route("api/[controller]")]
    [ApiController]
    public class GuessGameController : ControllerBase
    {
        private readonly ILogger<GuessGameController> logger;
        private readonly GuessGameManager gameManager;

        public GuessGameController(ILogger<GuessGameController> logger,
            GuessGameManager gameManager)
        {
            this.logger = logger;
            this.gameManager = gameManager;
        }

        [HttpGet("/Game")]
        public IActionResult Get()
        {
            return File("game.html", "text/html");
        }

        [HttpPost("Create")]
        public IActionResult CreateGame([FromBody]CreateGameRequest request)
        {
            if (request.Rounds <= 0)
            {
                return BadRequest("The number of rounds must be greater than 0.");
            }
            if (request.Rounds > 50)
            {
                return BadRequest("The number of rounds must be less than 50.");
            }
            GuessGameState gameState = new GuessGameState(request.Rounds);
            gameManager.AddGame(gameState);
            logger.LogInformation($"Created new game with code {gameState.Code} and {request.Rounds} rounds");
            return Ok(gameState.Code);
        }

        [HttpGet("Join/{gameCode}")]
        public IActionResult JoinGame(string gameCode)
        {
            bool validGame = gameManager.ContainsGame(gameCode);
            if (!validGame)
            {
                return BadRequest("That game code does not exist.");
            }
            return Ok();
        }

        public class CreateGameRequest
        {
            public int Rounds { get; set; }
        }
    }
}
