using System;
using System.Collections.Generic;
using System.Xml;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace DivineDragon.MapTools
{
    [Serializable]
    public class PersonInfo
    {
        public string Pid;
        public string Name;
        public string Jid;
        public string Fid;
        public string Aid;
        public int Gender;
        public int Level;
        public string UnitIconID;

        public bool IsFemale => Gender == 2;
    }
    
    [Serializable]
    public class JobInfo
    {
        public string Jid;
        public string Name;
        public string UnitIconID_M;
        public string UnitIconID_F;
        public string UnitIconWeaponID;
        public int MoveType;
        public int Rank;
    }
    
    public class DisposDataLoader
    {
        private static DisposDataLoader _instance;
        public static DisposDataLoader Instance
        {
            get
            {
                RegisterXmlWatchers();
                if (_instance == null)
                {
                    _instance = new DisposDataLoader();
                    _instance.LoadAllData();
                }
                return _instance;
            }
        }
        
        private Dictionary<string, PersonInfo> personData = new Dictionary<string, PersonInfo>();
        private Dictionary<string, JobInfo> jobData = new Dictionary<string, JobInfo>();
        private Dictionary<string, Texture2D> iconCache = new Dictionary<string, Texture2D>();
        private Dictionary<string, string> iconPathCache = new Dictionary<string, string>(); // key -> filename (no ext) or ""
        private string[] iconFiles = Array.Empty<string>();          // full paths
        private string[] iconFilesName = Array.Empty<string>();      // file name only
        private string[] iconFilesNameLower = Array.Empty<string>(); // lowercase name for comparison
        private Dictionary<string, string> mpidNameMap = new Dictionary<string, string>();
        private Dictionary<string, string> mjidNameMap = new Dictionary<string, string>();

        private static bool watchersRegistered;

        private const string PERSON_XML_PATH = MapToolsPaths.GameDataShareAssetPath + "/Person.xml";
        private const string JOB_XML_PATH = MapToolsPaths.GameDataShareAssetPath + "/Job.xml";
        private const string ICON_FOLDER = "Assets/Editor/Unit Icons and the Last Engage";
        private const string PERSON_BUNDLE_TXT = "Assets/person.bytes.bundle.txt";
        private const string JOB_BUNDLE_TXT = "Assets/job.bytes.bundle.txt";

        public void LoadAllData()
        {
            LoadPersonData();
            LoadPersonBundleNames();
            LoadJobData();
            LoadJobBundleNames();
            BuildIconIndex();
            Debug.Log($"Loaded {personData.Count} persons and {jobData.Count} jobs");
        }

        private void LoadPersonData()
        {
            if (!File.Exists(PERSON_XML_PATH))
            {
                Debug.LogError($"Person.xml not found at {PERSON_XML_PATH}");
                return;
            }

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(PERSON_XML_PATH);

                XmlNodeList dataNodes = doc.SelectNodes("//Sheet[@Name='個人']/Data/Param");

                foreach (XmlNode node in dataNodes)
                {
                    var person = new PersonInfo
                    {
                        Pid = node.Attributes["Pid"]?.Value ?? "",
                        Name = node.Attributes["Name"]?.Value ?? "",
                        Jid = node.Attributes["Jid"]?.Value ?? "",
                        Fid = node.Attributes["Fid"]?.Value ?? "",
                        Aid = node.Attributes["Aid"]?.Value ?? "",
                        Gender = ParseInt(node.Attributes["Gender"]?.Value, 1),
                        Level = ParseInt(node.Attributes["Level"]?.Value, 1),
                        UnitIconID = node.Attributes["UnitIconID"]?.Value ?? ""
                    };
                    
                    if (!string.IsNullOrEmpty(person.Pid))
                    {
                        personData[person.Pid] = person;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading Person.xml: {e.Message}");
            }
        }

        private void LoadPersonBundleNames()
        {
            mpidNameMap.Clear();
            if (!File.Exists(PERSON_BUNDLE_TXT))
            {
                // Optional file; skip silently
                return;
            }
            try
            {
                // Simple parser: blocks of [MPID_X] then one or more lines of text.
                string[] lines = File.ReadAllLines(PERSON_BUNDLE_TXT);
                string currentKey = null;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                void Flush()
                {
                    if (!string.IsNullOrEmpty(currentKey))
                    {
                        string text = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            // First line is the display name; later lines are description
                            string firstLine = text.Split('\n')[0].Trim();
                            mpidNameMap[currentKey] = firstLine;
                        }
                    }
                    currentKey = null;
                    sb.Length = 0;
                }

                foreach (var raw in lines)
                {
                    string line = raw.TrimEnd();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        Flush();
                        string key = line.Substring(1, line.Length - 2);
                        currentKey = key; // e.g., MPID_Lueur
                        continue;
                    }
                    if (currentKey != null)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(line);
                    }
                }
                Flush();
                Debug.Log($"Loaded {mpidNameMap.Count} MPID display names from bundle");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Error reading {PERSON_BUNDLE_TXT}: {e.Message}");
            }
        }

        private void LoadJobBundleNames()
        {
            mjidNameMap.Clear();
            if (!File.Exists(JOB_BUNDLE_TXT)) return;
            try
            {
                string[] lines = File.ReadAllLines(JOB_BUNDLE_TXT);
                string currentKey = null;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                void Flush()
                {
                    if (!string.IsNullOrEmpty(currentKey))
                    {
                        string text = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            string first = text.Split('\n')[0].Trim();
                            mjidNameMap[currentKey] = first;
                        }
                    }
                    currentKey = null; sb.Length = 0;
                }
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        Flush();
                        string key = line.Substring(1, line.Length - 2); // e.g., MJID_BraveHero
                        currentKey = key;
                        continue;
                    }
                    if (currentKey != null)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(line);
                    }
                }
                Flush();
                // Map MJID_H_* to same name as MJID_* if missing
                foreach (var kv in mjidNameMap.Where(kv => kv.Key.StartsWith("MJID_H_")).ToList())
                {
                    string baseKey = kv.Key.Replace("MJID_H_", "MJID_");
                    if (!mjidNameMap.ContainsKey(baseKey) && mjidNameMap.ContainsKey(kv.Key))
                        mjidNameMap[baseKey] = kv.Value;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error reading {JOB_BUNDLE_TXT}: {e.Message}");
            }
        }

        private void BuildIconIndex()
        {
            try
            {
                if (Directory.Exists(ICON_FOLDER))
                {
                    iconFiles = Directory.GetFiles(ICON_FOLDER, "*.png");
                    iconFilesName = iconFiles.Select(Path.GetFileName).ToArray();
                    iconFilesNameLower = iconFilesName.Select(n => n.ToLowerInvariant()).ToArray();
                }
                else
                {
                    iconFiles = Array.Empty<string>();
                    iconFilesName = Array.Empty<string>();
                    iconFilesNameLower = Array.Empty<string>();
                }
                iconPathCache.Clear();
                iconCache.Clear();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error building icon index from {ICON_FOLDER}: {e.Message}");
            }
        }
        
        private void LoadJobData()
        {
            if (!File.Exists(JOB_XML_PATH))
            {
                Debug.LogError($"Job.xml not found at {JOB_XML_PATH}");
                return;
            }

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(JOB_XML_PATH);

                XmlNodeList dataNodes = doc.SelectNodes("//Sheet[@Name='兵種']/Data/Param");

                foreach (XmlNode node in dataNodes)
                {
                    var job = new JobInfo
                    {
                        Jid = node.Attributes["Jid"]?.Value ?? "",
                        Name = node.Attributes["Name"]?.Value ?? "",
                        UnitIconID_M = node.Attributes["UnitIconID_M"]?.Value ?? "",
                        UnitIconID_F = node.Attributes["UnitIconID_F"]?.Value ?? "",
                        UnitIconWeaponID = node.Attributes["UnitIconWeaponID"]?.Value ?? "",
                        MoveType = ParseInt(node.Attributes["MoveType"]?.Value, 1),
                        Rank = ParseInt(node.Attributes["Rank"]?.Value, 0)
                    };
                    
                    if (!string.IsNullOrEmpty(job.Jid))
                    {
                        jobData[job.Jid] = job;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading Job.xml: {e.Message}");
            }
        }
        
        public PersonInfo GetPerson(string pid)
        {
            if (string.IsNullOrEmpty(pid))
                return null;
            
            personData.TryGetValue(pid, out PersonInfo person);
            return person;
        }

        public IEnumerable<PersonInfo> GetAllPersons()
        {
            return personData.Values;
        }

        public IEnumerable<JobInfo> GetAllJobs()
        {
            return jobData.Values;
        }
        
        public JobInfo GetJob(string jid)
        {
            if (string.IsNullOrEmpty(jid))
                return null;
            
            jobData.TryGetValue(jid, out JobInfo job);
            return job;
        }
        
        // Returns a list of candidate substrings to search for in the icon filenames
        private IEnumerable<string> GetUnitIconTokens(DisposEntry entry)
        {
            if (entry == null) yield break;
            var person = GetPerson(entry.Pid);
            string jid = !string.IsNullOrEmpty(entry.Jid) ? entry.Jid : person?.Jid;
            var job = GetJob(jid);

            // 1) Person specific icon id, if provided
            if (person != null && !string.IsNullOrEmpty(person.UnitIconID))
                yield return person.UnitIconID;

            // 2) English token from MPID_Name (e.g., MPID_Goldmary -> Goldmary)
            if (person != null && !string.IsNullOrEmpty(person.Name) && person.Name.StartsWith("MPID_"))
            {
                string english = person.Name.Substring(5);
                if (!string.IsNullOrWhiteSpace(english))
                {
                    yield return english;                 // e.g., Ivy, Goldmary
                    yield return "MPID_" + english;      // in case files keep MPID_ prefix
                }
            }

            // 3) Job-based icon id (current behavior)
            if (job != null)
            {
                string iconId = (person != null && person.IsFemale) ? job.UnitIconID_F : job.UnitIconID_M;
                if (string.IsNullOrEmpty(iconId)) iconId = job.UnitIconID_M;
                if (!string.IsNullOrEmpty(iconId)) yield return iconId;
            }
        }

        public string GetUnitIconPath(DisposEntry entry)
        {
            var person = GetPerson(entry.Pid);
            string jid = !string.IsNullOrEmpty(entry.Jid) ? entry.Jid : person?.Jid;
            var job = GetJob(jid);
            string weaponId = InferWeaponFromItems(entry) ?? job?.UnitIconWeaponID;
            if (string.IsNullOrEmpty(weaponId)) weaponId = "NoWeapon";

            // Cache key incorporates PID, JID choice and weapon id
            string cacheKey = $"{entry.Pid}|{jid}|{weaponId}";
            if (iconPathCache.TryGetValue(cacheKey, out var cachedPath))
                return string.IsNullOrEmpty(cachedPath) ? null : cachedPath;

            // Build candidate tokens
            var tokens = GetUnitIconTokens(entry).ToList();
            if (tokens.Count == 0 || iconFilesNameLower.Length == 0)
            {
                iconPathCache[cacheKey] = ""; // negative cache
                return null;
            }

            // 0) Prefer exact per-person + job + weapon filename when available
            string personId = GetPerson(entry.Pid)?.UnitIconID;
            string jobIconId = null;
            if (job != null)
            {
                jobIconId = (GetPerson(entry.Pid)?.IsFemale ?? false) ? job.UnitIconID_F : job.UnitIconID_M;
                if (string.IsNullOrEmpty(jobIconId)) jobIconId = job.UnitIconID_M;
            }
            if (!string.IsNullOrEmpty(personId) && !string.IsNullOrEmpty(jobIconId))
            {
                string want = ($"{personId}_{jobIconId}_{weaponId}").ToLowerInvariant();
                for (int i = 0; i < iconFilesNameLower.Length; i++)
                {
                    var nameLower = Path.GetFileNameWithoutExtension(iconFilesNameLower[i]);
                    if (nameLower == want)
                    {
                        string exact = Path.GetFileNameWithoutExtension(iconFilesName[i]);
                        iconPathCache[cacheKey] = exact;
                        return exact;
                    }
                }
            }

            int bestLen = int.MaxValue;
            int bestIndex = -1;
            foreach (var token in tokens)
            {
                if (string.IsNullOrEmpty(token)) continue;
                string tokenLower = token.ToLowerInvariant();
                for (int i = 0; i < iconFilesNameLower.Length; i++)
                {
                    string nameLower = iconFilesNameLower[i];
                    if (nameLower.Contains(tokenLower))
                    {
                        bool hasWeapon = !string.IsNullOrEmpty(weaponId) && nameLower.Contains("_" + weaponId.ToLowerInvariant());
                        int scoreLen = iconFilesName[i].Length + (hasWeapon ? 0 : 50); // prefer with weapon suffix
                        if (scoreLen < bestLen)
                        {
                            bestLen = scoreLen;
                            bestIndex = i;
                        }
                    }
                }
                if (bestIndex >= 0) break; // found a good match for this token
            }

            string result = null;
            if (bestIndex >= 0)
            {
                result = Path.GetFileNameWithoutExtension(iconFilesName[bestIndex]);
            }
            iconPathCache[cacheKey] = result ?? ""; // cache even misses
            return result;
        }

        private static string InferWeaponFromItems(DisposEntry entry)
        {
            if (entry == null || entry.Items == null) return null;
            // Look through items and try to guess the weapon family from common substrings
            foreach (var it in entry.Items)
            {
                var id = it?.Iid;
                if (string.IsNullOrEmpty(id)) continue;
                // English hints
                if (id.IndexOf("Lance", StringComparison.OrdinalIgnoreCase) >= 0) return "Lance";
                if (id.IndexOf("Sword", StringComparison.OrdinalIgnoreCase) >= 0) return "Sword";
                if (id.IndexOf("Axe", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("Ax", StringComparison.OrdinalIgnoreCase) >= 0) return "Ax";
                if (id.IndexOf("Bow", StringComparison.OrdinalIgnoreCase) >= 0) return "Bow";
                if (id.IndexOf("Knife", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("Dagger", StringComparison.OrdinalIgnoreCase) >= 0) return "Knife";
                if (id.IndexOf("Staff", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("Rod", StringComparison.OrdinalIgnoreCase) >= 0) return "Staff";
                if (id.IndexOf("Magic", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("Tome", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("Book", StringComparison.OrdinalIgnoreCase) >= 0) return "MagicBook";
                if (id.IndexOf("Scroll", StringComparison.OrdinalIgnoreCase) >= 0) return "Scroll";
                // Japanese hints
                if (id.Contains("槍")) return "Lance";
                if (id.Contains("剣")) return "Sword";
                if (id.Contains("斧")) return "Ax";
                if (id.Contains("弓")) return "Bow";
                if (id.Contains("短") || id.Contains("ナイフ")) return "Knife";
                if (id.Contains("杖")) return "Staff";
            }
            return null;
        }
        
        public Texture2D GetUnitIcon(DisposEntry entry)
        {
            string iconPath = GetUnitIconPath(entry);
            if (string.IsNullOrEmpty(iconPath))
                return null;
            
            if (iconCache.TryGetValue(iconPath, out Texture2D cached))
                return cached;
            
            string directPath = $"{ICON_FOLDER}/{iconPath}.png";
            if (File.Exists(directPath))
            {
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(directPath);
                if (texture != null)
                {
                    iconCache[iconPath] = texture;
                    return texture;
                }
            }
            
            return null;
        }
        
        public string GetUnitDisplayName(DisposEntry entry)
        {
            if (entry == null)
                return "Unknown";
            
            if (entry.IsGroupHeader)
                return entry.Group;
            
            var person = GetPerson(entry.Pid);
            if (person != null && !string.IsNullOrEmpty(person.Name))
            {
                // If Name looks like an MPID token, map it to human-readable
                // Common patterns: "MPID_..." or "$PID_..."; we check for MPID_ prefix
                if (person.Name.StartsWith("MPID_"))
                {
                    if (mpidNameMap.TryGetValue(person.Name, out var nice))
                        return nice;
                }
                return person.Name;
            }
            
            if (!string.IsNullOrEmpty(entry.Pid))
            {
                string pid = entry.Pid;
                if (pid.StartsWith("PID_"))
                    pid = pid.Substring(4);
                return pid;
            }
            
            return "Unknown";
        }
        
        public Color GetForceColor(int force)
        {
            switch (force)
            {
                case 0: return new Color(0.2f, 0.4f, 0.8f, 1f);
                case 1: return new Color(0.8f, 0.2f, 0.2f, 1f);
                case 2: return new Color(0.2f, 0.8f, 0.2f, 1f);
                case 3: return new Color(0.8f, 0.8f, 0.2f, 1f);
                default: return Color.gray;
            }
        }
        
        public string GetForceName(int force)
        {
            switch (force)
            {
                case 0: return "Player";
                case 1: return "Enemy";
                case 2: return "Ally";
                case 3: return "Other";
                default: return "Unknown";
            }
        }
        
        private int ParseInt(string value, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            
            if (int.TryParse(value, out int result))
                return result;
            
            return defaultValue;
        }
        
        public void ClearCache()
        {
            iconCache.Clear();
        }
        
        public void ReloadData()
        {
            personData.Clear();
            jobData.Clear();
            iconCache.Clear();
            mpidNameMap.Clear();
            LoadAllData();
        }

        public string GetDisplayNameFromMpid(string mpid)
        {
            if (string.IsNullOrEmpty(mpid)) return null;
            mpidNameMap.TryGetValue(mpid, out var name);
            return name;
        }

        public string GetDisplayNameFromMjid(string mjid)
        {
            if (string.IsNullOrEmpty(mjid)) return null;
            mjidNameMap.TryGetValue(mjid, out var name);
            return name;
        }

        public string GetJobDisplayName(JobInfo job)
        {
            if (job == null) return "Unknown";
            if (!string.IsNullOrEmpty(job.Name))
            {
                var nice = GetDisplayNameFromMjid(job.Name);
                if (!string.IsNullOrEmpty(nice)) return nice;
            }
            return job.Jid ?? "Unknown";
        }

        private static void RegisterXmlWatchers()
        {
            if (watchersRegistered)
            {
                return;
            }

            XmlAssetTracker.Register(PERSON_XML_PATH, OnPersonOrJobXmlChanged);
            XmlAssetTracker.Register(JOB_XML_PATH, OnPersonOrJobXmlChanged);
            watchersRegistered = true;
        }

        private static void OnPersonOrJobXmlChanged()
        {
            if (_instance != null)
            {
                Debug.Log("Detected Person/Job XML change. Reloading data.");
                _instance.ReloadData();
            }
        }

        
    }
}
