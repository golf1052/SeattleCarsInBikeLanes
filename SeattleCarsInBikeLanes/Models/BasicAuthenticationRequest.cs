namespace SeattleCarsInBikeLanes.Models
{
    public class BasicAuthenticationRequest : AdminRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
