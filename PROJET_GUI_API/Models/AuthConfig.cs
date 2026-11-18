namespace ApiTester.Models;

public class AuthConfig
{
    public AuthType Type { get; set; } = AuthType.None;
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ApiKeyName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public bool ApiKeyInHeader { get; set; } = true;
}
