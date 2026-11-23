using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KSC.MeetingManagement.Application.Media;
using Whisper.net;

namespace KSC.MeetingManagement.Infrastructure.Media;

/// <summary>Runs FFmpeg and Whisper locally over a completed LiveKit Egress recording.</summary>
public sealed class LocalMeetingTranscriptionService(
    MediaOptions options,
    TimeProvider clock) : IMeetingTranscriptionService
{
    private const long MaxAudioBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan RecordingSettleTime = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim TranscriptionGate = new(1, 1);

    public bool IsConfigured => options.TranscriptionConfigured
        && File.Exists(options.TranscriptionModelPath)
        && Directory.Exists(options.EffectiveTranscriptionRecordingPath);

    public async Task<MeetingTranscript> TranscribeAsync(string room, Guid meetingId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentOutOfRangeException.ThrowIfEqual(meetingId, Guid.Empty);
        EnsureConfigured();

        if (await GetLatestAsync(room, meetingId, cancellationToken) is { } latest)
            return latest;

        var recording = await FindRecordingAsync(room, cancellationToken);
        var transcriptPath = GetTranscriptPath(Path.GetFileNameWithoutExtension(recording));
        var lockPath = transcriptPath + ".lock";
        await using var lockStream = await AcquireLockAsync(lockPath, cancellationToken);
        if (await ReadTranscriptAsync(transcriptPath, meetingId, cancellationToken) is { } existing)
        {
            return existing;
        }

        await TranscriptionGate.WaitAsync(cancellationToken);
        try
        {
            var wavPath = Path.Combine(Path.GetTempPath(), $"ksc-transcript-{Guid.NewGuid():N}.wav");
            try
            {
                await DecodeAudioAsync(recording, wavPath, cancellationToken);
                var transcript = await RunWhisperAsync(wavPath, meetingId, cancellationToken);
                var serialized = JsonSerializer.SerializeToUtf8Bytes(transcript);
                await SaveTranscriptAsync(transcriptPath, serialized, cancellationToken);
                return transcript;
            }
            finally
            {
                TryDelete(wavPath);
            }
        }
        finally
        {
            TranscriptionGate.Release();
        }
    }

    public async Task<MeetingTranscript?> GetLatestAsync(string room, Guid meetingId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentOutOfRangeException.ThrowIfEqual(meetingId, Guid.Empty);
        var directory = options.EffectiveTranscriptionRecordingPath;
        var outputDirectory = options.EffectiveTranscriptionOutputPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
            || !Directory.Exists(outputDirectory)) return null;

        var prefix = $"{room}-";
        foreach (var path in Directory.EnumerateFiles(outputDirectory, prefix + "*.transcript.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transcript = await ReadTranscriptAsync(path, meetingId, cancellationToken);
            if (transcript is not null) return transcript;
        }

        return null;
    }

    private void EnsureConfigured()
    {
        if (!options.TranscriptionConfigured)
            throw new InvalidOperationException("Local transcription is not configured.");
        if (!Directory.Exists(options.EffectiveTranscriptionRecordingPath))
            throw new DirectoryNotFoundException("Transcription recording directory is not mounted in the API container.");
        Directory.CreateDirectory(options.EffectiveTranscriptionOutputPath);
        if (!File.Exists(options.TranscriptionModelPath))
            throw new FileNotFoundException("The local Whisper model was not found.", options.TranscriptionModelPath);
    }

    private async Task<string> FindRecordingAsync(string room, CancellationToken cancellationToken)
    {
        var directory = options.EffectiveTranscriptionRecordingPath!;
        var prefix = $"{room}-";
        var wait = TimeSpan.FromSeconds(Math.Clamp(options.TranscriptionFileWaitSeconds, 1, 600));
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < wait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = Directory.EnumerateFiles(directory, prefix + "*.mp4")
                .Where(file => new FileInfo(file).Length > 0)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            foreach (var file in files)
            {
                if (File.Exists(GetTranscriptPath(Path.GetFileNameWithoutExtension(file)))) continue;
                if (await IsStableAsync(file, cancellationToken)) return file;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TranscriptionNotReadyException("No completed server-side recording was found for this meeting.");
    }

    private static async Task<bool> IsStableAsync(string path, CancellationToken cancellationToken)
    {
        var first = new FileInfo(path);
        var length = first.Length;
        var modified = first.LastWriteTimeUtc;
        await Task.Delay(RecordingSettleTime, cancellationToken);
        var current = new FileInfo(path);
        return current.Exists && current.Length == length && current.LastWriteTimeUtc == modified;
    }

    private async Task DecodeAudioAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(inputPath);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxAudioBytes)
            throw new InvalidDataException("Recording is missing, empty, or exceeds the 2 GB transcription limit.");

        var startInfo = new ProcessStartInfo
        {
            FileName = options.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("16000");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("wav");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg failed to start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("FFmpeg is not installed or FfmpegPath is invalid.", exception);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        _ = await stderrTask;
        _ = await stdoutTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException("FFmpeg could not extract audio from the server-side recording.");
        }
    }

    private async Task<MeetingTranscript> RunWhisperAsync(string wavPath, Guid meetingId, CancellationToken cancellationToken)
    {
        using var factory = WhisperFactory.FromPath(options.TranscriptionModelPath!);
        using var processor = factory.CreateBuilder()
            .WithLanguage(options.TranscriptionLanguage)
            .Build();
        await using var stream = File.OpenRead(wavPath);
        var segments = new List<TranscriptSegment>();
        var text = new StringBuilder();

        await foreach (var result in processor.ProcessAsync(stream, cancellationToken))
        {
            var segmentText = result.Text.Trim();
            if (segmentText.Length == 0) continue;
            segments.Add(new TranscriptSegment(result.Start, result.End, segmentText));
            if (text.Length > 0) text.Append(' ');
            text.Append(segmentText);
        }

        return new MeetingTranscript(meetingId, options.TranscriptionLanguage, clock.GetUtcNow(), segments, text.ToString());
    }

    private static async Task<MeetingTranscript?> ReadTranscriptAsync(string path, Guid meetingId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var transcript = await JsonSerializer.DeserializeAsync<MeetingTranscript>(stream, cancellationToken: cancellationToken);
            return transcript?.MeetingId == meetingId ? transcript : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task SaveTranscriptAsync(string transcriptPath, byte[] contents, CancellationToken cancellationToken)
    {
        var temporaryPath = transcriptPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, contents, cancellationToken);
            File.Move(temporaryPath, transcriptPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private string GetTranscriptPath(string recordingName) =>
        Path.Combine(options.EffectiveTranscriptionOutputPath, $"{recordingName}.transcript.json");

    private static async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                if (stopwatch.Elapsed >= TimeSpan.FromMinutes(30))
                    throw new TimeoutException("Another transcription request is still running for this recording.");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
