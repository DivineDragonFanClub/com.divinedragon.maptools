using System;
using System.Collections.Generic;
using System.Xml;
using System.IO;
using UnityEngine;

namespace DivineDragon.MapTools
{
    [System.Flags]
    public enum DisposFlags
    {
        Normal = 1,
        Hard = 2,
        Lunatic = 4,
        Create = 8,
        Leader = 16,
        NotMove = 32,
        Edge = 64,
        Pos = 128,
        Must = 256,
        Fix = 512,
        Guest = 1024,
        MaskSortie = 896,       // Pos | Must | Fix
        MaskDifficulty = 7      // Normal | Hard | Lunatic
    }
    [Serializable]
    public class DisposEntry
    {
        public string Group;
        public string Pid;
        public int Force;
        public int Flag;
        public int AppearX;
        public int AppearY;
        public int DisposX;
        public int DisposY;
        public int Direction;
        public int LevelN;
        public int LevelH;
        public int LevelL;
        public string Jid;
        public string Sid;
        public string Bid;
        public string Gid;
        public int HpStockCount;
        public int[] States = new int[6];
        
        public class ItemData
        {
            public string Iid;
            public int Drop;
        }
        
        public ItemData[] Items = new ItemData[6];
        
        public string AI_ActionName;
        public string AI_ActionVal;
        public string AI_MindName;
        public string AI_MindVal;
        public string AI_AttackName;
        public string AI_AttackVal;
        public string AI_MoveName;
        public string AI_MoveVal;
        public string AI_BattleRate;
        public int AI_Priority;
        public int AI_HealRateA;
        public int AI_HealRateB;
        public int AI_BandNo;
        public string AI_MoveLimit;
        public int AI_Flag;
        public string AI_Active;
        public int AI_ActiveTurn;
        public string AI_ActiveFlag;
        
        // Simplified item accessors for compatibility
        public string Item1 { get => Items[0]?.Iid; set => Items[0].Iid = value; }
        public string Item2 { get => Items[1]?.Iid; set => Items[1].Iid = value; }
        public string Item3 { get => Items[2]?.Iid; set => Items[2].Iid = value; }
        public string Item4 { get => Items[3]?.Iid; set => Items[3].Iid = value; }
        public string Item5 { get => Items[4]?.Iid; set => Items[4].Iid = value; }
        public string Item6 { get => Items[5]?.Iid; set => Items[5].Iid = value; }
        
        private Dictionary<string, string> additionalAttributes = new Dictionary<string, string>();
        
        public DisposEntry()
        {
            for (int i = 0; i < 6; i++)
            {
                Items[i] = new ItemData();
                States[i] = -1;
            }
            States[0] = 0;
        }
        
        public void SetAdditionalAttribute(string key, string value)
        {
            additionalAttributes[key] = value;
        }
        
        public string GetAdditionalAttribute(string key)
        {
            return additionalAttributes.TryGetValue(key, out string value) ? value : "";
        }
        
        public Dictionary<string, string> GetAllAdditionalAttributes()
        {
            return new Dictionary<string, string>(additionalAttributes);
        }
        
        // In-memory header flag; do not infer from PID so units can have empty PIDs safely
        public bool IsGroupHeader { get; set; }
        
        public Vector2Int GetDisposPosition() => new Vector2Int(DisposX, DisposY);
        public Vector2Int GetAppearPosition() => new Vector2Int(AppearX, AppearY);
    }
    
    [Serializable]
    public class DisposGroup
    {
        public string GroupName;
        public List<DisposEntry> Entries = new List<DisposEntry>();
        public bool IsExpanded = true;
        public bool IsVisible = true;
    }
    
    public class DisposDocument
    {
        public List<DisposGroup> Groups = new List<DisposGroup>();
        public List<DisposEntry> AllEntries = new List<DisposEntry>();
        public string FilePath;
        
        private XmlDocument xmlDoc;
        private List<XmlNode> headerParams = new List<XmlNode>();
        
        public static DisposDocument LoadFromFile(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"Dispos file not found: {path}");
                return null;
            }
            
            var doc = new DisposDocument();
            doc.FilePath = path;
            
            try
            {
                doc.xmlDoc = new XmlDocument();
                doc.xmlDoc.PreserveWhitespace = true;
                doc.xmlDoc.Load(path);
                
                XmlNode headerNode = doc.xmlDoc.SelectSingleNode("//Sheet/Header");
                if (headerNode != null)
                {
                    foreach (XmlNode param in headerNode.ChildNodes)
                    {
                        if (param.NodeType == XmlNodeType.Element)
                        {
                            doc.headerParams.Add(param.Clone());
                        }
                    }
                }
                
                XmlNodeList dataNodes = doc.xmlDoc.SelectNodes("//Sheet/Data/Param");
                
                DisposGroup currentGroup = null;
                
                foreach (XmlNode node in dataNodes)
                {
                    var entry = ParseEntryFromXml(node);
                    doc.AllEntries.Add(entry);
                    
                    if (entry.IsGroupHeader)
                    {
                        currentGroup = new DisposGroup
                        {
                            GroupName = entry.Group,
                            Entries = new List<DisposEntry>()
                        };
                        doc.Groups.Add(currentGroup);
                        Debug.Log($"Found group header: {entry.Group}");
                    }
                    else if (currentGroup != null)
                    {
                        // Assign group name in-memory for convenience
                        entry.Group = currentGroup.GroupName;
                        currentGroup.Entries.Add(entry);
                        Debug.Log($"Added entry to group {currentGroup.GroupName}: {entry.Pid} at ({entry.DisposX}, {entry.DisposY})");
                    }
                    else
                    {
                        Debug.LogWarning($"Entry {entry.Pid} has no group!");
                    }
                }
                
                Debug.Log($"Loaded {doc.Groups.Count} groups with {doc.AllEntries.Count} total entries from {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading Dispos file: {e.Message}");
                return null;
            }
            
            return doc;
        }
        
        private static DisposEntry ParseEntryFromXml(XmlNode node)
        {
            var entry = new DisposEntry();
            
            entry.Group = node.Attributes["Group"]?.Value ?? "";
            entry.Pid = node.Attributes["Pid"]?.Value ?? "";
            entry.Force = ParseInt(node.Attributes["Force"]?.Value);
            entry.Flag = ParseInt(node.Attributes["Flag"]?.Value);
            entry.AppearX = ParseInt(node.Attributes["AppearX"]?.Value);
            entry.AppearY = ParseInt(node.Attributes["AppearY"]?.Value);
            entry.DisposX = ParseInt(node.Attributes["DisposX"]?.Value);
            entry.DisposY = ParseInt(node.Attributes["DisposY"]?.Value);
            entry.Direction = ParseInt(node.Attributes["Direction"]?.Value);
            entry.LevelN = ParseInt(node.Attributes["LevelN"]?.Value);
            entry.LevelH = ParseInt(node.Attributes["LevelH"]?.Value);
            entry.LevelL = ParseInt(node.Attributes["LevelL"]?.Value);
            entry.Jid = node.Attributes["Jid"]?.Value ?? "";
            entry.Sid = node.Attributes["Sid"]?.Value ?? "";
            entry.Bid = node.Attributes["Bid"]?.Value ?? "";
            entry.Gid = node.Attributes["Gid"]?.Value ?? "";
            entry.HpStockCount = ParseInt(node.Attributes["HpStockCount"]?.Value);
            
            for (int i = 0; i < 6; i++)
            {
                entry.States[i] = ParseInt(node.Attributes[$"State{i}"]?.Value, -1);
                entry.Items[i].Iid = node.Attributes[$"Item{i+1}.Iid"]?.Value ?? "";
                entry.Items[i].Drop = ParseInt(node.Attributes[$"Item{i+1}.Drop"]?.Value);
            }
            
            entry.AI_ActionName = node.Attributes["AI_ActionName"]?.Value ?? "";
            entry.AI_ActionVal = node.Attributes["AI_ActionVal"]?.Value ?? "";
            entry.AI_MindName = node.Attributes["AI_MindName"]?.Value ?? "";
            entry.AI_MindVal = node.Attributes["AI_MindVal"]?.Value ?? "";
            entry.AI_AttackName = node.Attributes["AI_AttackName"]?.Value ?? "";
            entry.AI_AttackVal = node.Attributes["AI_AttackVal"]?.Value ?? "";
            entry.AI_MoveName = node.Attributes["AI_MoveName"]?.Value ?? "";
            entry.AI_MoveVal = node.Attributes["AI_MoveVal"]?.Value ?? "";
            entry.AI_BattleRate = node.Attributes["AI_BattleRate"]?.Value ?? "";
            entry.AI_Priority = ParseInt(node.Attributes["AI_Priority"]?.Value);
            entry.AI_HealRateA = ParseInt(node.Attributes["AI_HealRateA"]?.Value);
            entry.AI_HealRateB = ParseInt(node.Attributes["AI_HealRateB"]?.Value);
            entry.AI_BandNo = ParseInt(node.Attributes["AI_BandNo"]?.Value);
            entry.AI_MoveLimit = node.Attributes["AI_MoveLimit"]?.Value ?? "";
            entry.AI_Flag = ParseInt(node.Attributes["AI_Flag"]?.Value);
            entry.AI_Active = node.Attributes["AI_Active"]?.Value ?? "";
            entry.AI_ActiveTurn = ParseInt(node.Attributes["AI_ActiveTurn"]?.Value);
            entry.AI_ActiveFlag = node.Attributes["AI_ActiveFlag"]?.Value ?? "";
            
            foreach (XmlAttribute attr in node.Attributes)
            {
                if (!IsKnownAttribute(attr.Name))
                {
                    entry.SetAdditionalAttribute(attr.Name, attr.Value);
                }
            }

            // Determine header from persisted format: non-empty Group with empty Pid
            entry.IsGroupHeader = !string.IsNullOrEmpty(entry.Group) && string.IsNullOrEmpty(entry.Pid);
            
            return entry;
        }
        
        private static bool IsKnownAttribute(string name)
        {
            var knownAttrs = new HashSet<string>
            {
                "Group", "Pid", "Force", "Flag", "AppearX", "AppearY", "DisposX", "DisposY",
                "Direction", "LevelN", "LevelH", "LevelL", "Jid", "Sid", "Bid", "Gid", 
                "HpStockCount", "State0", "State1", "State2", "State3", "State4", "State5",
                "Item1.Iid", "Item1.Drop", "Item2.Iid", "Item2.Drop", "Item3.Iid", "Item3.Drop",
                "Item4.Iid", "Item4.Drop", "Item5.Iid", "Item5.Drop", "Item6.Iid", "Item6.Drop",
                "AI_ActionName", "AI_ActionVal", "AI_MindName", "AI_MindVal", "AI_AttackName",
                "AI_AttackVal", "AI_MoveName", "AI_MoveVal", "AI_BattleRate", "AI_Priority",
                "AI_HealRateA", "AI_HealRateB", "AI_BandNo", "AI_MoveLimit", "AI_Flag",
                "AI_Active", "AI_ActiveTurn", "AI_ActiveFlag"
            };
            return knownAttrs.Contains(name);
        }
        
        private static int ParseInt(string value, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(value))
                return defaultValue;
            
            if (int.TryParse(value, out int result))
                return result;
            
            return defaultValue;
        }
        
        public void SaveToFile()
        {
            SaveToFile(FilePath);
        }
        
        public void SaveToFile(string path)
        {
            try
            {
                if (xmlDoc == null)
                {
                    CreateNewXmlDocument();
                }
                
                XmlNode dataNode = xmlDoc.SelectSingleNode("//Sheet/Data");
                dataNode.RemoveAll();
                
                foreach (var entry in AllEntries)
                {
                    XmlElement paramElement = xmlDoc.CreateElement("Param");
                    WriteEntryToXml(paramElement, entry);
                    dataNode.AppendChild(paramElement);
                }
                
                xmlDoc.Save(path);
                Debug.Log($"Saved Dispos file to {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error saving Dispos file: {e.Message}");
            }
        }
        
        private void CreateNewXmlDocument()
        {
            xmlDoc = new XmlDocument();
            XmlDeclaration xmlDeclaration = xmlDoc.CreateXmlDeclaration("1.0", "utf-8", null);
            xmlDoc.AppendChild(xmlDeclaration);
            
            XmlElement book = xmlDoc.CreateElement("Book");
            book.SetAttribute("Count", "1");
            xmlDoc.AppendChild(book);
            
            XmlElement sheet = xmlDoc.CreateElement("Sheet");
            sheet.SetAttribute("Name", "配置");
            sheet.SetAttribute("Count", AllEntries.Count.ToString());
            book.AppendChild(sheet);
            
            XmlElement header = xmlDoc.CreateElement("Header");
            sheet.AppendChild(header);
            
            foreach (var param in headerParams)
            {
                header.AppendChild(xmlDoc.ImportNode(param, true));
            }
            
            XmlElement data = xmlDoc.CreateElement("Data");
            sheet.AppendChild(data);
        }
        
        private void WriteEntryToXml(XmlElement element, DisposEntry entry)
        {
            // Persist in canonical format: headers carry Group name + empty Pid; units carry empty Group
            string groupAttr = entry.IsGroupHeader ? (entry.Group ?? "") : "";
            string pidAttr = entry.IsGroupHeader ? "" : (entry.Pid ?? "");
            element.SetAttribute("Group", groupAttr);
            element.SetAttribute("Pid", pidAttr);
            if (entry.IsGroupHeader)
            {
                // Write blank values for header rows to match source style
                element.SetAttribute("Force", "");
                element.SetAttribute("Flag", "");
                element.SetAttribute("AppearX", "");
                element.SetAttribute("AppearY", "");
                element.SetAttribute("DisposX", "");
                element.SetAttribute("DisposY", "");
                element.SetAttribute("Direction", "");
                element.SetAttribute("LevelN", "");
                element.SetAttribute("LevelH", "");
                element.SetAttribute("LevelL", "");
                element.SetAttribute("Jid", "");
            }
            else
            {
                element.SetAttribute("Force", entry.Force.ToString());
                element.SetAttribute("Flag", entry.Flag.ToString());
                element.SetAttribute("AppearX", entry.AppearX.ToString());
                element.SetAttribute("AppearY", entry.AppearY.ToString());
                element.SetAttribute("DisposX", entry.DisposX.ToString());
                element.SetAttribute("DisposY", entry.DisposY.ToString());
                element.SetAttribute("Direction", entry.Direction.ToString());
                element.SetAttribute("LevelN", entry.LevelN.ToString());
                element.SetAttribute("LevelH", entry.LevelH.ToString());
                element.SetAttribute("LevelL", entry.LevelL.ToString());
                element.SetAttribute("Jid", entry.Jid);
            }
            
            for (int i = 0; i < 6; i++)
            {
                if (entry.IsGroupHeader)
                {
                    element.SetAttribute($"Item{i+1}.Iid", "");
                    element.SetAttribute($"Item{i+1}.Drop", "");
                }
                else
                {
                    element.SetAttribute($"Item{i+1}.Iid", entry.Items[i].Iid);
                    element.SetAttribute($"Item{i+1}.Drop", entry.Items[i].Drop.ToString());
                }
            }
            
            if (entry.IsGroupHeader)
            {
                element.SetAttribute("Sid", "");
                element.SetAttribute("Bid", "");
                element.SetAttribute("Gid", "");
                element.SetAttribute("HpStockCount", "");
            }
            else
            {
                element.SetAttribute("Sid", entry.Sid);
                element.SetAttribute("Bid", entry.Bid);
                element.SetAttribute("Gid", entry.Gid);
                element.SetAttribute("HpStockCount", entry.HpStockCount.ToString());
            }
            
            for (int i = 0; i < 6; i++)
            {
                element.SetAttribute($"State{i}", entry.IsGroupHeader ? "" : entry.States[i].ToString());
            }
            
            if (entry.IsGroupHeader)
            {
                element.SetAttribute("AI_ActionName", "");
                element.SetAttribute("AI_ActionVal", "");
                element.SetAttribute("AI_MindName", "");
                element.SetAttribute("AI_MindVal", "");
                element.SetAttribute("AI_AttackName", "");
                element.SetAttribute("AI_AttackVal", "");
                element.SetAttribute("AI_MoveName", "");
                element.SetAttribute("AI_MoveVal", "");
                element.SetAttribute("AI_BattleRate", "");
                element.SetAttribute("AI_Priority", "");
                element.SetAttribute("AI_HealRateA", "");
                element.SetAttribute("AI_HealRateB", "");
                element.SetAttribute("AI_BandNo", "");
                element.SetAttribute("AI_MoveLimit", "");
                element.SetAttribute("AI_Flag", "");
                element.SetAttribute("AI_Active", "");
                element.SetAttribute("AI_ActiveTurn", "");
                element.SetAttribute("AI_ActiveFlag", "");
            }
            else
            {
                element.SetAttribute("AI_ActionName", entry.AI_ActionName);
                element.SetAttribute("AI_ActionVal", entry.AI_ActionVal);
                element.SetAttribute("AI_MindName", entry.AI_MindName);
                element.SetAttribute("AI_MindVal", entry.AI_MindVal);
                element.SetAttribute("AI_AttackName", entry.AI_AttackName);
                element.SetAttribute("AI_AttackVal", entry.AI_AttackVal);
                element.SetAttribute("AI_MoveName", entry.AI_MoveName);
                element.SetAttribute("AI_MoveVal", entry.AI_MoveVal);
                element.SetAttribute("AI_BattleRate", entry.AI_BattleRate);
                element.SetAttribute("AI_Priority", entry.AI_Priority.ToString());
                element.SetAttribute("AI_HealRateA", entry.AI_HealRateA.ToString());
                element.SetAttribute("AI_HealRateB", entry.AI_HealRateB.ToString());
                element.SetAttribute("AI_BandNo", entry.AI_BandNo.ToString());
                element.SetAttribute("AI_MoveLimit", entry.AI_MoveLimit);
                element.SetAttribute("AI_Flag", entry.AI_Flag.ToString());
                element.SetAttribute("AI_Active", entry.AI_Active ?? "");
                element.SetAttribute("AI_ActiveTurn", entry.AI_ActiveTurn.ToString());
                element.SetAttribute("AI_ActiveFlag", entry.AI_ActiveFlag ?? "");
            }
            
            foreach (var kvp in entry.GetAllAdditionalAttributes())
            {
                element.SetAttribute(kvp.Key, kvp.Value);
            }
        }
    }
}
