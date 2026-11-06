using System.Collections;
using GameNetcodeStuff;
using Sanity.Symptoms;
using UnityEngine;

namespace Sanity;

// Plays phantom sounds at points in the world, on this computer only.
// Each sound copies the real source's own audio settings (volume, falloff, distance, mixer) and gets the
// game's wall-muffling component if the real one has it, so it behaves like the real thing.
// Unlike real sounds, it is never relayed through walkie-talkies and makes no "noise" monsters can hear.
// Louder than the real thing (at real loudness, steps and sounds were too easy to miss): every phantom in the
// world falls off as if it were CloserFactor x PhantomLoudness (config) times closer than it is. The direction and
// the wall muffling stay true; only the distance falloff is stretched (works for every rolloff kind, and never
// depends on volumes above 1).
// Also the hidden object other symptoms run their timing on (Run).
internal sealed class PhantomAudio : MonoBehaviour
{
    private static PhantomAudio? instance;

    // Phantoms sound this many times closer than they are, times the PhantomLoudness config (default 1).
    internal const float CloserFactor = 1.8f;
    internal static BepInEx.Configuration.ConfigEntry<float>? Loudness;

    internal static float Closer => CloserFactor * (Loudness != null ? Mathf.Clamp(Loudness.Value, 0.25f, 4f) : 1f);

    // One phantom in the world at a time: phantom sound and phantom steps share this slot.
    internal static bool IsPlaying { get; private set; }

    // Take the slot for a phantom that plays itself (phantom steps). False if one is already playing.
    internal static bool TryClaim()
    {
        if (instance == null || IsPlaying)
        {
            return false;
        }
        IsPlaying = true;
        return true;
    }

    // Give the slot back (always, in a finally).
    internal static void Release()
    {
        IsPlaying = false;
    }

    // Run a symptom's timing (a coroutine) on this hidden object. False if it doesn't exist (nothing runs).
    internal static bool Run(IEnumerator routine)
    {
        if (instance == null)
        {
            return false;
        }
        instance.StartCoroutine(routine);
        return true;
    }

    internal static void Create()
    {
        GameObject host = new GameObject("Sanity.PhantomAudio");
        host.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(host);
        instance = host.AddComponent<PhantomAudio>();
    }

    // positions: one per repeat (all the same point, unless the sound is approaching).
    internal static bool Play(PlayerControllerB player, MonsterSound sound, AudioClip[] clips,
                              AudioSource? template, Vector3[] positions)
    {
        if (instance == null || IsPlaying || clips.Length == 0 || positions.Length == 0)
        {
            return false;
        }

        IsPlaying = true;
        instance.StartCoroutine(instance.PlayMonster(player, sound, clips, template, positions));
        return true;
    }

    // A positional sound source at a point, copying a real source's settings and its wall muffling.
    // The caller destroys the returned object when done.
    // louder: a phantom (stretched falloff, see the top); false for real objects' sounds (the fake door's key drop).
    // extra: louder still for this one (the Presence's voice), on top of the usual stretch.
    internal static AudioSource CreateSource(string name, AudioSource? template, Vector3 position, bool louder = true, float extra = 1f)
    {
        GameObject host = new GameObject(name);
        host.transform.position = position;
        AudioSource source = host.AddComponent<AudioSource>();
        CopySettings(template, source);
        if (louder)
        {
            Stretch(source, Closer * Mathf.Max(1f, extra));
        }

        OccludeAudio? templateOcclude = template != null ? template.GetComponent<OccludeAudio>() : null;
        if (templateOcclude != null)
        {
            // A real source's muffling is already settled; ours is brand new and would start unmuffled
            // for a moment. So set the starting muffling ourselves (the game's component reuses this filter).
            AudioLowPassFilter lowPass = host.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = StartingCutoff(position, source.maxDistance);

            OccludeAudio occlude = host.AddComponent<OccludeAudio>();
            occlude.useReverb = templateOcclude.useReverb;
        }
        return source;
    }

    // A coroutine: a function Unity runs a bit at a time. "yield return WaitForSeconds" pauses it
    // without freezing the game, so repeats can be spaced out over a few seconds.
    private IEnumerator PlayMonster(PlayerControllerB player, MonsterSound sound, AudioClip[] clips,
                                    AudioSource? template, Vector3[] positions)
    {
        // Everything inside "try" is guaranteed to be cleaned up by "finally", even if something fails.
        GameObject? host = null;
        float started = Time.time;
        try
        {
            AudioSource source = CreateSource("Sanity.PhantomSound", template, positions[0]);
            host = source.gameObject;

            AudioClip? lastClip = null;
            for (int i = 0; i < positions.Length; i++)
            {
                if (Gone(player))
                {
                    yield break;
                }

                host.transform.position = positions[i];
                AudioClip clip = clips[Random.Range(0, clips.Length)];
                if (clip != null)
                {
                    source.pitch = Random.Range(sound.MinPitch, sound.MaxPitch);
                    source.PlayOneShot(clip, Random.Range(sound.MinVolume, sound.MaxVolume));
                    lastClip = clip;
                }

                if (i < positions.Length - 1)
                {
                    float gap = Random.Range(sound.MinGap, sound.MaxGap);
                    if (sound.GapAfterClip && clip != null)
                    {
                        gap += clip.length / Mathf.Max(0.1f, source.pitch);
                    }
                    for (float t = 0f; t < gap; t += Time.deltaTime)
                    {
                        yield return null;
                        if (Gone(player))
                        {
                            yield break;
                        }
                    }
                }
            }

            // Let the last clip finish before cleaning up.
            float tail = (lastClip != null ? lastClip.length / Mathf.Max(0.1f, source.pitch) : 0f) + 0.25f;
            for (float t = 0f; t < tail; t += Time.deltaTime)
            {
                yield return null;
                if (Gone(player))
                {
                    yield break;
                }
            }
        }
        finally
        {
            if (host != null)
            {
                Destroy(host);
            }
            IsPlaying = false;

            // How long it really ran (clips, repeats, gaps), to judge long sounds like breathing.
            float lasted = Time.time - started;
            Telemetry.Event("PHANTOM_SOUND_END", lasted, sound.Name + " lasted " + lasted.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s");
        }
    }

    // Stop a sound (destroying its source cuts it off) once the player died, left the moon, or the ship is taking off.
    // Checked every frame, not just between repeats: one breathing clip is ~20 s. The ship taking off counts as gone,
    // or a sound started in the ship would play on into orbit.
    private static bool Gone(PlayerControllerB? player)
    {
        StartOfRound? startOfRound = StartOfRound.Instance;
        return player == null || player.isPlayerDead || !SanityMeter.OnMoon
               || (startOfRound != null && startOfRound.shipIsLeaving);
    }

    // The same formula the game's OccludeAudio uses: muffled if a wall is between the sound and the listener.
    private static float StartingCutoff(Vector3 position, float maxDistance)
    {
        StartOfRound startOfRound = StartOfRound.Instance;
        if (startOfRound == null || startOfRound.audioListener == null)
        {
            return 10000f;
        }

        Vector3 listener = startOfRound.audioListener.transform.position;
        if (!Physics.Linecast(position, listener, 256, QueryTriggerInteraction.Ignore))
        {
            return 10000f;
        }

        float distance = Vector3.Distance(listener, position);
        return Mathf.Clamp(2500f / (distance / (Mathf.Max(1f, maxDistance) / 2f)), 900f, 4000f);
    }

    // Falloff as if the sound were "factor" times closer: every rolloff kind (logarithmic, linear, and custom curves,
    // which run over 0..maxDistance) gives at distance d what it gave at d / factor.
    private static void Stretch(AudioSource source, float factor)
    {
        if (factor <= 1.001f)
        {
            return;
        }
        source.minDistance *= factor;
        source.maxDistance *= factor;
        source.volume = 1f;
    }

    private static void CopySettings(AudioSource? from, AudioSource to)
    {
        to.playOnAwake = false;
        to.loop = false;
        to.spatialBlend = 1f;    // always positional: the whole point is where it comes from

        if (from == null)
        {
            to.rolloffMode = AudioRolloffMode.Logarithmic;
            to.minDistance = 2f;
            to.maxDistance = 40f;
            return;
        }

        to.outputAudioMixerGroup = from.outputAudioMixerGroup;
        to.volume = from.volume > 0.05f ? from.volume : 1f;   // some sources start silent and get turned up at runtime
        to.rolloffMode = from.rolloffMode;
        to.minDistance = from.minDistance;
        to.maxDistance = from.maxDistance;
        to.dopplerLevel = from.dopplerLevel;
        to.spread = from.spread;
        to.priority = from.priority;
        to.reverbZoneMix = from.reverbZoneMix;
        if (from.rolloffMode == AudioRolloffMode.Custom)
        {
            to.SetCustomCurve(AudioSourceCurveType.CustomRolloff, from.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
        }
    }
}
