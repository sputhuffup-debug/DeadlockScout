namespace DeadlockScout.Core.Models;

public sealed record HeroAsset(int Id, string ClassName, string Name, string? ImageUrl, bool Playable);

/// <summary>A shop item or (Slot "ability") a hero ability. Ids are 32-bit unsigned and reach ~4.3 billion, hence long.</summary>
public sealed record ItemAsset(long Id, string ClassName, string Name, string Slot, int Tier, int Cost, string? ImageUrl);
