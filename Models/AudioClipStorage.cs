using System.Collections.Concurrent;
using System.IO;
using System.Threading;

public class AudioClipStorage
{
    public static Dictionary<string, AudioClipData> AudioClips { get; } = new Dictionary<string, AudioClipData>();

    public static bool LoadClip(byte[] rawData, string name)
    {
        if (rawData == null || rawData.Length == 0)
        {
            ServerConsole.AddLog("[AudioPlayer] Failed loading clip because raw data is null or empty!");
            return false;
        }

        if (AudioClips.ContainsKey(name))
        {
            ServerConsole.AddLog($"[AudioPlayer] Failed loading clip because clip with {name} is already loaded!");
            return false;
        }

        float[] samples = null;
        int sampleRate = 0;
        int channels = 0;

        using (MemoryStream ms = new MemoryStream(rawData))
        {
            using (VorbisReader reader = new VorbisReader(ms))
            {
                sampleRate = reader.SampleRate;
                channels = reader.Channels;

                samples = new float[reader.TotalSamples * channels];
                reader.ReadSamples(samples);
            }
        }

        AudioClips.Add(name, new AudioClipData(name, sampleRate, channels, samples));
        return true;
    }

    public static bool LoadClip(string path, string name = null)
    {
        if (!File.Exists(path))
        {
            ServerConsole.AddLog($"[AudioPlayer] Failed loading clip from {path} because file not exists!");
            return false;
        }

        if (string.IsNullOrEmpty(name))
            name = Path.GetFileNameWithoutExtension(path);

        if (AudioClips.ContainsKey(name))
        {
            ServerConsole.AddLog($"[AudioPlayer] Failed loading clip from {path} because clip with {name} is already loaded!");
            return false;
        }

        string extension = Path.GetExtension(path);

        float[] samples = null;
        int sampleRate = 0;
        int channels = 0;

        switch (extension)
        {
            case ".ogg":
                using (VorbisReader reader = new VorbisReader(path))
                {
                    sampleRate = reader.SampleRate;
                    channels = reader.Channels;

                    samples = new float[reader.TotalSamples * channels];

                    reader.ReadSamples(samples, 0, samples.Length);
                }
                break;
            default:
                ServerConsole.AddLog($"[AudioPlayer] Failed loading clip from {path} because clip is not supported! ( extension {extension} )");
                return false;
        }

        AudioClips.Add(name, new AudioClipData(name, sampleRate, channels, samples));
        return true;
    }

    /// <summary>
    /// Asynchronously loads an audio clip from disk on a background thread.
    /// The decoded clip is enqueued and applied on the main thread by <see cref="DrainPendingLoads"/>.
    /// </summary>
    public static void LoadClipAsync(string path, string name = null, Action<bool> onComplete = null, AudioPlayer autoPlayOn = null, float volume = 1f)
    {
        if (!File.Exists(path))
        {
            ServerConsole.AddLog($"[AudioPlayer] Failed loading clip from {path} because file not exists!");
            onComplete?.Invoke(false);
            return;
        }

        if (string.IsNullOrEmpty(name))
            name = Path.GetFileNameWithoutExtension(path);

        string extension = Path.GetExtension(path);
        if (extension != ".ogg")
        {
            ServerConsole.AddLog($"[AudioPlayer] Failed loading clip from {path} because clip is not supported! ( extension {extension} )");
            onComplete?.Invoke(false);
            return;
        }

        string clipName = name;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                float[] samples;
                int sampleRate;
                int channels;

                using (var reader = new VorbisReader(path))
                {
                    sampleRate = reader.SampleRate;
                    channels = reader.Channels;
                    samples = new float[reader.TotalSamples * channels];
                    reader.ReadSamples(samples, 0, samples.Length);
                }

                var data = new AudioClipData(clipName, sampleRate, channels, samples);
                _pendingLoads.Enqueue(new PendingLoad { Name = clipName, Data = data, Callback = onComplete, AutoPlayer = autoPlayOn, Volume = volume });
            }
            catch (Exception ex)
            {
                ServerConsole.AddLog($"[AudioPlayer] Async load failed for {path}: {ex.Message}");
                onComplete?.Invoke(false);
            }
        });
    }

    /// <summary>
    /// Must be called from the main thread (e.g. in Update or a MEC coroutine) to apply pending async loads.
    /// </summary>
    public static void DrainPendingLoads()
    {
        while (_pendingLoads.TryDequeue(out PendingLoad item))
        {
            if (AudioClips.ContainsKey(item.Name))
                AudioClips.Remove(item.Name);

            AudioClips.Add(item.Name, item.Data);
            item.AutoPlayer?.RemoveAllClips();
            item.AutoPlayer?.AddClip(item.Name, item.Volume);
            item.Callback?.Invoke(true);
        }
    }

    private struct PendingLoad
    {
        public string Name;
        public AudioClipData Data;
        public Action<bool> Callback;
        public AudioPlayer AutoPlayer;
        public float Volume;
    }

    private static ConcurrentQueue<PendingLoad> _pendingLoads = new ConcurrentQueue<PendingLoad>();

    public static bool DestroyClip(string name)
    {
        if (!AudioClips.ContainsKey(name))
        {
            ServerConsole.AddLog($"[AudioPlayer] Clip with name {name} is not loaded!");
            return false;
        }

        return AudioClips.Remove(name);
    }
}
