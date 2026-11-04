using IDMRea.Core;
using Xunit;
namespace IDMRea.Tests;

public sealed class FfmpegTests
{
    private sealed class Runner(int exit = 0, string errors = "") : IToolRunner
    {
        public string? Exe { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }
        public Task<ToolRun> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken token)
        { Exe = exe; Args = args; return Task.FromResult(new ToolRun(exit, "", errors)); }
    }

    [Fact]
    public async Task StreamsAreCopiedIntoTheChosenContainerWithoutReencoding()
    {
        var runner = new Runner();
        await new Ffmpeg(runner, @"C:\tools\ffmpeg\ffmpeg.exe").JoinAsync([@"D:\v.part", @"D:\a.part"], @"D:\out.part", "mkv", default);
        Assert.Equal(@"C:\tools\ffmpeg\ffmpeg.exe", runner.Exe);
        Assert.Equal(["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", @"D:\v.part", "-i", @"D:\a.part",
            "-map", "0:v?", "-map", "0:a?", "-map", "1:v?", "-map", "1:a?", "-c", "copy", "-f", "matroska", @"D:\out.part"], runner.Args);
    }

    [Theory]
    [InlineData("mp4", "mp4")]
    [InlineData("m4a", "mp4")]
    [InlineData("webm", "webm")]
    [InlineData("mkv", "matroska")]
    public async Task EachContainerHasItsMuxer(string container, string muxer)
    {
        var runner = new Runner();
        await new Ffmpeg(runner, "ffmpeg.exe").JoinAsync(["v"], "out", container, default);
        Assert.Equal(muxer, runner.Args![runner.Args.ToList().IndexOf("-f") + 1]);
    }

    [Fact]
    public async Task AFailedJoinSaysWhatFfmpegSaid()
    {
        var error = await Assert.ThrowsAsync<IOException>(() =>
            new Ffmpeg(new Runner(1, "[mp4 @ 0x1] Could not find tag for codec vp9\nConversion failed!\n"), "ffmpeg.exe").JoinAsync(["v"], "out", "mp4", default));
        Assert.Equal("FFmpeg couldn't join the video: Could not find tag for codec vp9", error.Message);
    }
}
