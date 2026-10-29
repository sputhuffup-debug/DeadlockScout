using System.IO;
using System.Media;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Cues;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Services;

/// <summary>
/// Plays the audio cues: short tone patterns synthesised here (no sound files, nothing of the game's), one per cue, at the
/// configured volume, through the default output device. Only our own process makes a sound; the game is never touched.
/// </summary>
public sealed class AudioCuePlayer
{
    private const int Rate = 44100;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly ILogger _log;
    private readonly Dictionary<(Cue, int), byte[]> _wavs = new();
    private SoundPlayer? _playing; // held so the sound's buffer outlives Play(), which returns at once

    public AudioCuePlayer(OverlayState state, IOptionsMonitor<ScoutSettings> settings, ILogger<AudioCuePlayer> log)
    {
        _settings = settings;
        _log = log;
        state.AudioSettings = () => _settings.CurrentValue.Audio;
        state.CuesFired += cues => { foreach (var cue in cues.Take(1)) Play(cue); }; // one sound at a time: the first is the most urgent
    }

    /// <summary>Plays one cue now, whatever the switches say (the main window's Test button uses this too).</summary>
    public void Play(Cue cue)
    {
        try
        {
            int volume = (int)Math.Round(Math.Clamp(_settings.CurrentValue.Audio.Volume, 0, 1) * 100);
            if (volume == 0) return;
            if (!_wavs.TryGetValue((cue, volume), out var wav)) _wavs[(cue, volume)] = wav = Wav(Notes(cue), volume / 100.0);
            _playing = new SoundPlayer(new MemoryStream(wav, writable: false));
            _playing.Play(); // asynchronous; a newer cue cuts an older one off
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not play the {Cue} cue", cue); }
    }

    /// <summary>Each cue's notes (frequency in Hz, length in ms): rising for push now, a three-note chime for objectives, falling and low for pace.</summary>
    public static IReadOnlyList<(double Hz, int Ms)> Notes(Cue cue) => cue switch
    {
        Cue.PushNow => new[] { (880.0, 90), (0.0, 40), (1175.0, 120) },
        Cue.Objective => new[] { (660.0, 110), (880.0, 110), (1320.0, 180) },
        _ => new[] { (392.0, 160), (0.0, 50), (294.0, 220) },
    };

    /// <summary>A 16-bit mono WAV of <paramref name="notes"/> (0 Hz is a rest), each with a short fade in and out so it never clicks.</summary>
    public static byte[] Wav(IReadOnlyList<(double Hz, int Ms)> notes, double volume)
    {
        var samples = new List<short>();
        foreach (var (hz, length) in notes)
        {
            int n = Rate * length / 1000, fade = Math.Min(n / 4, Rate * 8 / 1000);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - 1 - i) / (double)Math.Max(1, fade));
                double v = hz <= 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / Rate) * env * volume * 0.5;
                samples.Add((short)Math.Round(v * short.MaxValue));
            }
        }
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int data = samples.Count * 2;
            w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(data);
            foreach (var s in samples) w.Write(s);
        }
        return ms.ToArray();
    }
}
