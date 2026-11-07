using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Sanity;

// Sends the anonymous stats (DayStats): on by default (config Stats/ShareAnonymousStats), but only once the player has
// been shown the notice on the main menu (StatsPanel), which offers to turn it off. Until then nothing is sent. Each
// day's packet is saved to a small queue in the mod's own folder ("stats/queue/"), then sent in the background, one at a time, to Endpoint: a Google Apps Script that appends it as
// one row of a spreadsheet. A packet that can't be sent (offline, game closing) waits in the queue and goes on the next
// launch or after the next day. Each packet that was sent is moved to "stats/sent/" (the newest 10 are kept), so anyone
// can read exactly what left their computer. Never blocks the game; fails silently.
internal sealed class StatsUploader : MonoBehaviour
{
    // The web address of the spreadsheet's script. Empty: nothing is sent or queued.
    private const string Endpoint = "https://script.google.com/macros/s/AKfycbwQDVFC-WLSu4X3dYIfbt7ZFUOAGuHpVeLs-ZpYdUkR0MnenP5qQSXYGQ_qUw3WhGDO/exec";
    private const int MaxQueued = 40;                  // oldest packets are dropped beyond this
    private const int MaxKeptSent = 10;                // copies of sent packets kept for the player to read
    private const int MaxPacketBytes = 32 * 1024;      // a packet is 2-4 KB; anything bigger is a bug, not sent
    private const int MaxPerRun = 10;
    private const int TimeoutSeconds = 20;
    private const float FirstTryDelay = 10f;           // after launch, once the game has settled

    // Raise this when what's collected changes in a way players should hear about: the notice is shown again.
    private const int NoticeVersion = 1;
    private const string NoticeKey = "Lowlight.Sanity.StatsNoticeVersion";   // in the game's own saved preferences

    private static StatsUploader? instance;
    private static bool? noticeSeen;
    private static bool sending;
    private static bool warned;

    internal static bool Sharing => Plugin.ShareStats != null && Plugin.ShareStats.Value && Endpoint.Length > 0 && NoticeSeen;

    // Whether this player has been shown the current notice. Kept in the game's own saved preferences, not the config,
    // so a modpack's shared config can't skip it and mod updates don't reset it.
    internal static bool NoticeSeen
    {
        get
        {
            if (noticeSeen == null)
            {
                try
                {
                    noticeSeen = PlayerPrefs.GetInt(NoticeKey, 0) >= NoticeVersion;
                }
                catch (Exception)
                {
                    noticeSeen = false;
                }
            }
            return noticeSeen.Value;
        }
    }

    internal static void MarkNoticeSeen()
    {
        noticeSeen = true;
        try
        {
            PlayerPrefs.SetInt(NoticeKey, NoticeVersion);
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Warn(e);
        }
    }

    // Whether this build can send at all (for the panel and the debug menu).
    internal static bool Available => Endpoint.Length > 0;

    private static string Folder => Path.Combine(Telemetry.ModFolder, "stats");

    private static string QueueFolder => Path.Combine(Folder, "queue");

    private static string SentFolder => Path.Combine(Folder, "sent");

    internal static void Create()
    {
        GameObject host = new GameObject("Sanity.StatsUploader");
        host.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(host);
        instance = host.AddComponent<StatsUploader>();
        Plugin.ShareStats.SettingChanged += (_, _) =>
        {
            if (!Plugin.ShareStats.Value)
            {
                ClearQueue();
            }
        };
    }

    // Turned off: packets still waiting are deleted, so nothing more leaves the computer.
    private static void ClearQueue()
    {
        try
        {
            foreach (string path in ReadQueue())
            {
                File.Delete(path);
            }
        }
        catch (Exception e)
        {
            Warn(e);
        }
    }

    // A finished day's packet: queue it, then try to send what's waiting.
    internal static void Submit(string packet)
    {
        if (!Sharing || packet.Length > MaxPacketBytes)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(QueueFolder);
            string name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{DayStats.SessionId}.json";
            File.WriteAllText(Path.Combine(QueueFolder, name), packet, Encoding.UTF8);
            string[] queued = Directory.GetFiles(QueueFolder, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < queued.Length - MaxQueued; i++)
            {
                File.Delete(queued[i]);
            }
        }
        catch (Exception e)
        {
            Warn(e);
            return;
        }
        SendWaiting();
    }

    // Sends whatever is waiting, in the background (does nothing if sending is off or already running).
    internal static void SendWaiting()
    {
        if (!Sharing || sending || instance == null)
        {
            return;
        }
        instance.StartCoroutine(instance.SendAll());
    }

    private void Start()
    {
        StartCoroutine(SendOnLaunch());
    }

    private IEnumerator SendOnLaunch()
    {
        yield return new WaitForSeconds(FirstTryDelay);
        SendWaiting();
    }

    private IEnumerator SendAll()
    {
        sending = true;
        try
        {
            string[] queued = ReadQueue();
            foreach (string path in queued.Take(MaxPerRun))
            {
                string? packet = ReadPacket(path);
                if (packet == null)
                {
                    continue;
                }

                using UnityWebRequest request = new UnityWebRequest(Endpoint, UnityWebRequest.kHttpVerbPOST);
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(packet));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "text/plain");
                request.timeout = TimeoutSeconds;
                request.redirectLimit = 0;   // the script's redirect already means "received" (see Delivered)
                yield return request.SendWebRequest();

                if (!Delivered(request))
                {
                    break;   // offline or the endpoint is down: try again later, keep the rest in order
                }
                Done(path);
            }
        }
        finally
        {
            sending = false;
        }
    }

    // Google's script answers with a redirect to its result page; the row is already written by then, so the redirect
    // itself counts as delivered. It isn't followed: following it can re-send the packet to the wrong address and look
    // like a failure, which would mean sending (and saving) the same row again and again.
    private static bool Delivered(UnityWebRequest request)
    {
        long code = request.responseCode;
        return request.result == UnityWebRequest.Result.Success || code == 302 || code == 303;
    }

    private static string[] ReadQueue()
    {
        try
        {
            return Directory.Exists(QueueFolder)
                ? Directory.GetFiles(QueueFolder, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
        }
        catch (Exception e)
        {
            Warn(e);
            return Array.Empty<string>();
        }
    }

    private static string? ReadPacket(string path)
    {
        try
        {
            string packet = File.ReadAllText(path, Encoding.UTF8);
            if (packet.Length == 0 || packet.Length > MaxPacketBytes)
            {
                File.Delete(path);
                return null;
            }
            return packet;
        }
        catch (Exception e)
        {
            Warn(e);
            return null;
        }
    }

    // Sent: move it from the queue to the "sent" folder (same name, which starts with when it was made), and keep only
    // the newest few there.
    private static void Done(string path)
    {
        try
        {
            Directory.CreateDirectory(SentFolder);
            string target = Path.Combine(SentFolder, Path.GetFileName(path));
            if (File.Exists(target))
            {
                File.Delete(target);
            }
            File.Move(path, target);
            string[] sent = Directory.GetFiles(SentFolder, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < sent.Length - MaxKeptSent; i++)
            {
                File.Delete(sent[i]);
            }
        }
        catch (Exception e)
        {
            Warn(e);
        }
    }

    private static void Warn(Exception e)
    {
        if (!warned)
        {
            warned = true;
            Plugin.Log.LogWarning($"Stats: couldn't save or send ({e.GetType().Name}: {e.Message}). Nothing else is affected.");
        }
    }
}
