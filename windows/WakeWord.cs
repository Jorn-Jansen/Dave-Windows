using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NAudio.Wave;
using Vosk;

namespace DaveWindows;

/// <summary>
/// Listens offline for the wake phrase ("hey dave" by default, or anything you set). Nothing is sent anywhere until it hears it.
/// Runs on its own audio thread, so speech recognition never slows down or freezes Dave's screen.
/// </summary>
public sealed class WakeWord : IDisposable
{
    private static Model? sharedModel;
    private static readonly object ModelLock = new();

    /// <summary>The offline model, loaded once and shared (also used to check wake phrases in the settings).</summary>
    public static Model SharedModel
    {
        get
        {
            lock (ModelLock)
            {
                if (sharedModel == null)
                {
                    Vosk.Vosk.SetLogLevel(-1);
                    sharedModel = new Model(Path.Combine(AppContext.BaseDirectory, "model-en-us"));
                }
                return sharedModel;
            }
        }
    }

    /// <summary>Lower case, no punctuation, and Dutch spellings the English model doesn't know ("hé" -> "hey").</summary>
    public static string Normalize(string phrase)
    {
        var s = phrase.ToLowerInvariant().Replace("é", "e").Replace("è", "e").Replace("ë", "e").Replace("’", "'");
        s = Regex.Replace(s, @"[^a-z0-9' ]", " ");
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w is "he" or "hee" or "hej" ? "hey" : w);
        return string.Join(' ', words);
    }

    /// <summary>Words of [phrase] the offline listener can't recognise (it only knows English words).</summary>
    public static List<string> UnknownWords(string phrase) =>
        Normalize(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => SharedModel.FindWord(w) < 0).Distinct().ToList();

    private readonly VoskRecognizer recognizer;
    private readonly WaveInEvent input;
    private readonly Action onWake;
    private readonly string phrase;
    private readonly List<string> phrases;
    private readonly object sync = new();
    private bool disposed;
    private DateTime lastTrigger = DateTime.MinValue;

    /// <summary>While true (Dave is listening to you or talking), wake words are ignored.</summary>
    public volatile bool Paused;
    private bool wasDeaf; // skipped audio since the last chunk, so start fresh before listening again

    /// <param name="onWake">Called on the audio thread; marshal to the UI yourself.</param>
    public WakeWord(Settings settings, Action onWake)
    {
        this.onWake = onWake;
        phrase = Normalize(settings.EffectiveWakePhrase);
        var unknown = UnknownWords(phrase);
        if (unknown.Count > 0 || phrase.Length == 0)
        {
            // Words the model doesn't know would make the phrase impossible to hear; use the default instead.
            var fallback = Normalize($"hey {settings.Name}");
            if (UnknownWords(fallback).Count > 0) fallback = "hey dave";
            Log.Write($"Wake phrase '{phrase}' has unknown words ({string.Join(", ", unknown)}); listening for '{fallback}' instead");
            phrase = fallback;
        }

        // "hey" is often heard as "hay" or "hi"; accept those too.
        phrases = new List<string> { phrase };
        if (phrase.StartsWith("hey "))
            foreach (var alt in new[] { "hay ", "hi " }) phrases.Add(alt + phrase[4..]);

        // The phrase(s), plus single words and [unk]: near-misses land there instead of triggering Dave.
        var words = phrases.SelectMany(p => p.Split(' '));
        var grammar = phrases.Concat(words).Append("[unk]").Distinct().ToList();
        recognizer = new VoskRecognizer(SharedModel, 16000f, JsonSerializer.Serialize(grammar));

        // Created off the UI thread on purpose: NAudio then delivers audio on its own thread
        // instead of queueing every chunk (and the recognition work) onto Dave's screen thread.
        input = Task.Run(() => new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 }).Result;
        input.DataAvailable += OnAudio;
        input.StartRecording();
        Log.Write($"Wake word listening for '{phrase}'");
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        string heard;
        lock (sync)
        {
            if (disposed) return; // audio can still arrive while shutting down; the recognizer may be gone
            // Never while you're talking to Dave, or while Dave is talking (or just stopped): his own voice can't wake him.
            if (Paused || Speaker.IsBusy) { wasDeaf = true; return; }
            if (wasDeaf) { wasDeaf = false; recognizer.Reset(); } // forget half-heard sound from before
            string json, key;
            if (recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded)) { json = recognizer.Result(); key = "text"; }
            else { json = recognizer.PartialResult(); key = "partial"; }
            heard = (JsonNode.Parse(json)?[key]?.GetValue<string>() ?? "").Trim();
            // Only the wake phrase on its own, at the start of what you say: not somewhere in the middle of talking.
            if (!phrases.Contains(heard)) return;
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
            recognizer.Dispose(); // the shared model stays loaded for the next listener
        }
    }
}
