using System.Collections.Generic;
using FMOD;
using FMODUnity;
using UnityEngine.SceneManagement;

namespace UnityVRModFix;

// One-off diagnostic tool, not a gameplay fix: logs which FMOD sound is playing, tagged with the
// current scene name, whenever a NEW sound starts (diffed against last frame, so it doesn't spam
// the log for sounds still playing). The dialogue/music .bank files only expose internal sample
// names as hashes (e.g. "00122b6"), with no per-scene metadata - this lets us capture which exact
// audio files play while walking through a specific scene, by reading it straight from FMOD's low
// level API instead of trying to reverse-engineer the compiled Yarn dialogue data.
internal static class VoiceLoggerFix
{
    internal static bool Enabled;

    private static readonly HashSet<string> _playingLastFrame = new();

    internal static void Tick()
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var system = RuntimeManager.CoreSystem;
            if (system.getMasterChannelGroup(out var master) != RESULT.OK)
            {
                return;
            }

            var stillPlaying = new HashSet<string>();
            Collect(master, stillPlaying, 0);

            foreach (var name in stillPlaying)
            {
                if (!_playingLastFrame.Contains(name))
                {
                    var scene = SceneManager.GetActiveScene().name;
                    VRModFixLog.Info($"[VoiceLoggerFix] scene='{scene}' sound started: {name}");
                }
            }

            _playingLastFrame.Clear();
            foreach (var n in stillPlaying)
            {
                _playingLastFrame.Add(n);
            }
        }
        catch (System.Exception ex)
        {
            Enabled = false;
            VRModFixLog.Info($"[VoiceLoggerFix] Disabled after an error: {ex.Message}");
        }
    }

    // A ChannelGroup only lists the channels attached directly to it. The game's dialogue and music
    // go through sub-groups (buses) of the master group, so the whole tree has to be walked.
    private static void Collect(ChannelGroup group, HashSet<string> sink, int depth)
    {
        group.getNumChannels(out var numChannels);
        for (var i = 0; i < numChannels; i++)
        {
            if (group.getChannel(i, out var channel) != RESULT.OK
                || channel.isPlaying(out var isPlaying) != RESULT.OK || !isPlaying
                || channel.getCurrentSound(out var sound) != RESULT.OK || !sound.hasHandle()
                || sound.getName(out var name, 256) != RESULT.OK || string.IsNullOrEmpty(name))
            {
                continue;
            }
            sink.Add(name);
        }

        if (depth >= 8)
        {
            return;
        }
        group.getNumGroups(out var numGroups);
        for (var i = 0; i < numGroups; i++)
        {
            if (group.getGroup(i, out var child) == RESULT.OK)
            {
                Collect(child, sink, depth + 1);
            }
        }
    }
}
