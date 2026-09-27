using System.Text.Json.Nodes;
using NAudio.Wave;
using Vosk;

namespace DaveWindows;

/// <summary>
/// Listens offline for "Hey Dave" (Vosk). Nothing is sent anywhere until it hears it.
/// Runs on its own audio thread, so speech recognition never slows down or freezes Dave's screen.
/// </summary>
public sealed class WakeWord : IDisposable
{
    private static readonly string[] Phrases = { "hey dave", "hi dave", "okay dave", "hay dave" };
    // Near-miss words give the recognizer somewhere else to go, so "hey" alone doesn't trigger.
    private const string Grammar = """["hey dave", "hi dave", "okay dave", "hay dave", "hey", "hi", "okay", "dave", "day", "play", "[unk]"]""";

    private readonly Model model;
    private readonly VoskRecognizer recognizer;
    private readonly WaveInEvent input;
    private readonly Action onWake;
    private readonly object sync = new();
    private bool disposed;
    private DateTime lastTrigger = DateTime.MinValue;

    /// <summary>While true (Dave is listening or talking), wake words are ignored.</summary>
    public volatile bool Paused;

    /// <param name="onWake">Called on the audio thread; marshal to the UI yourself.</param>
    public WakeWord(Action onWake)
    {
        this.onWake = onWake;
        Vosk.Vosk.SetLogLevel(-1);
        model = new Model(Path.Combine(AppContext.BaseDirectory, "model-en-us"));
        recognizer = new VoskRecognizer(model, 16000f, Grammar);

        // Created off the UI thread on purpose: NAudio then delivers audio on its own thread
        // instead of queueing every chunk (and the recognition work) onto Dave's screen thread.
        input = Task.Run(() => new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 }).Result;
        input.DataAvailable += OnAudio;
        input.StartRecording();
        Log.Write("Wake word listening");
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        if (Paused) return;
        string heard;
        lock (sync)
        {
            if (disposed) return; // audio can still arrive while shutting down; the recognizer may be gone
            string json, key;
            if (recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded)) { json = recognizer.Result(); key = "text"; }
            else { json = recognizer.PartialResult(); key = "partial"; }
            heard = (JsonNode.Parse(json)?[key]?.GetValue<string>() ?? "").Trim();
            if (heard.Length == 0 || !Phrases.Any(heard.EndsWith)) return;
            if (DateTime.Now - lastTrigger < TimeSpan.FromSeconds(3)) return;
            lastTrigger = DateTime.Now;
            recognizer.Reset();
        }
        Log.Write($"Wake word heard: {heard}");
        onWake();
    }

    public void Dispose()
    {
        input.DataAvailable -= OnAudio;
        input.StopRecording();
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            input.Dispose();
            recognizer.Dispose();
            model.Dispose();
        }
    }
}
