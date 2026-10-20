namespace MusicPlayer.Library;

/// <summary>Satu kali lagu diputar: lagunya dan kapan diputar.</summary>
public sealed record PlayRecord(Track Track, DateTime PlayedUtc);

/// <summary>Lagu beserta berapa kali sudah diputar, untuk daftar Most Played.</summary>
public sealed record PlayCount(Track Track, int Count, DateTime LastPlayedUtc);
