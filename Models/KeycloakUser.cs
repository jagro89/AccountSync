namespace AccountSync.Models;

public sealed class KeycloakUser
{
    public required string Id { get; init; }
    public string? Username { get; init; }
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public Dictionary<string, string[]>? Attributes { get; init; }
}
