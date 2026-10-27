namespace DeadlockScout.Core.Models;

public sealed record HeroAsset(int Id, string ClassName, string Name, string? ImageUrl, bool Playable);

public sealed record ItemAsset(int Id, string ClassName, string Name, string Slot, int Tier, int Cost, string? ImageUrl);
