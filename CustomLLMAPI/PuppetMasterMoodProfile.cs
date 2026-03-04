using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

public class PuppetMasterMoodProfile
{
    [Serializable]
    public class BlendShapeTarget
    {
        public string meshName;
        public string blendShapeName;
        public float weight = 100f;
    }

    [Serializable]
    public class MoodGroup
    {
        public string name;
        public List<BlendShapeTarget> targets = new List<BlendShapeTarget>();
    }

    [Serializable]
    public class AvatarProfile
    {
        public string avatarName; // matched against VRM meta name or filename
        public List<MoodGroup> moods = new List<MoodGroup>();
    }

    public List<AvatarProfile> profiles = new List<AvatarProfile>();

    private static string GetProfilePath() =>
        Path.Combine(Application.dataPath, "..", "MoodProfile.json");

    public static PuppetMasterMoodProfile Load()
    {
        string path = GetProfilePath();
        if (!File.Exists(path))
        {
            Debug.LogWarning("[PuppetMaster] No MoodProfile.json found at: " + path + " — creating default.");
            var def = CreateDefault();
            def.Save();
            return def;
        }
        try
        {
            string json = File.ReadAllText(path);
            var profile = JsonConvert.DeserializeObject<PuppetMasterMoodProfile>(json);
            Debug.Log("[PuppetMaster] Loaded MoodProfile.json with " + profile.profiles.Count + " avatar profiles.");
            return profile;
        }
        catch (Exception ex)
        {
            Debug.LogError("[PuppetMaster] Failed to load MoodProfile.json: " + ex.Message);
            return CreateDefault();
        }
    }

    public void Save()
    {
        string path = GetProfilePath();
        try
        {
            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(path, json);
            Debug.Log("[PuppetMaster] Saved MoodProfile.json to: " + path);
        }
        catch (Exception ex)
        {
            Debug.LogError("[PuppetMaster] Failed to save MoodProfile.json: " + ex.Message);
        }
    }

    // Find best matching profile for the current avatar name
    public AvatarProfile GetProfileFor(string avatarDisplayName)
    {
        if (string.IsNullOrEmpty(avatarDisplayName)) return null;

        // Exact match first
        foreach (var p in profiles)
            if (string.Equals(p.avatarName, avatarDisplayName, StringComparison.OrdinalIgnoreCase))
                return p;

        // Partial match (e.g. "Lazuli" matches "Lazuli_v2")
        foreach (var p in profiles)
            if (avatarDisplayName.IndexOf(p.avatarName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                p.avatarName.IndexOf(avatarDisplayName, StringComparison.OrdinalIgnoreCase) >= 0)
                return p;

        return null;
    }

    private static PuppetMasterMoodProfile CreateDefault()
    {
        return new PuppetMasterMoodProfile
        {
            profiles = new List<AvatarProfile>
            {
                new AvatarProfile
                {
                    avatarName = "Lazuli",
                    moods = new List<MoodGroup>
                    {
                        new MoodGroup
                        {
                            name = "Joy",
                            targets = new List<BlendShapeTarget>
                            {
                                new BlendShapeTarget { meshName = "Body", blendShapeName = "Mouth_Grin_L", weight = 100f },
                                new BlendShapeTarget { meshName = "Body", blendShapeName = "Mouth_Grin_R", weight = 100f },
                                new BlendShapeTarget { meshName = "Body", blendShapeName = "Eye_Smile",    weight = 100f }
                            }
                        },
                        new MoodGroup { name = "Angry",   targets = new List<BlendShapeTarget>() },
                        new MoodGroup { name = "Sorrow",  targets = new List<BlendShapeTarget>() },
                        new MoodGroup { name = "Fun",     targets = new List<BlendShapeTarget>() },
                        new MoodGroup { name = "Neutral", targets = new List<BlendShapeTarget>() }
                    }
                }
            }
        };
    }
}