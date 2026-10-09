using System.Diagnostics;
using MusicPlayer.App;
using MusicPlayer.Core;
using Xunit;

namespace MusicPlayer.Playback.Tests;

public sealed class FormatIntegrationTests
{
    [Theory]
    [InlineData("mp3")]
    [InlineData("wav")]
    [InlineData("wave")]
    [InlineData("flac")]
    [InlineData("aac")]
    [InlineData("m4a")]
    [InlineData("m4b")]
    [InlineData("ogg")]
    [InlineData("oga")]
    [InlineData("opus")]
    [InlineData("wma")]
    [InlineData("ape")]
    [InlineData("wv")]
    [InlineData("tta")]
    [InlineData("mpc")]
    [InlineData("aiff")]
    [InlineData("aif")]
    [InlineData("dsf")]
    [InlineData("dff")]
    public async Task Representative_codec_decodes_and_seeks(string extension)
    {
        var root = Environment.GetEnvironmentVariable("MUSICPLAYER_MEDIA_FIXTURES")
            ?? throw new InvalidOperationException("Run Prepare-MediaFixtures.ps1 and set MUSICPLAYER_MEDIA_FIXTURES before the full format suite.");
        var file = new FileInfo(Path.Combine(root, "fixture." + extension));
        Assert.True(file.Exists, "Genuine format fixture is missing.");
        Assert.True(LibraryScanner.IsSupportedAudioFile(file.FullName));
        var track = new Track(file.FullName, extension, "", "", "", "", 0, 0,
            TimeSpan.Zero, file.Length, file.LastWriteTimeUtc, DateTime.UtcNow);
        using var playback = new PlaybackService("--aout=dummy", "--no-video-title-show");
        var failed = false;
        playback.PlaybackFailed += _ => failed = true;
        playback.Play(track);
        await WaitFor(() => playback.IsPlaying && playback.Position > 300, () => failed);
        Assert.True(playback.Duration > 1000, "The decoder did not report a media duration.");
        playback.Seek(3000);
        await WaitFor(() => playback.Position > 3100 && playback.IsPlaying, () => failed);
        playback.Pause();
        await WaitFor(() => !playback.IsPlaying, () => failed);
        playback.PlayLoaded();
        await WaitFor(() => playback.IsPlaying, () => failed);
    }

    private static async Task WaitFor(Func<bool> ready, Func<bool> failed)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            Assert.False(failed(), "The shipped LibVLC build rejected this format.");
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12), "Native decoder state did not become ready.");
            await Task.Delay(25);
        }
    }
}
