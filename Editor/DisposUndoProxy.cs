using UnityEngine;
using UnityEditor;
using System.Linq;

namespace DivineDragon.MapTools
{
    // ScriptableObject proxy to enable undo/redo for DisposDocument
    // Since DisposEntry is not a UnityEngine.Object, we need this proxy
    public class DisposUndoProxy : ScriptableObject
    {
        [SerializeField]
        private DisposDocument document;
        
        // Store the document state as JSON for undo tracking
        [SerializeField, HideInInspector]
        private string serializedState = "";
        
        public DisposDocument Document 
        { 
            get => document;
            set
            {
                document = value;
                // Don't serialize on initial set
            }
        }
        
        // Call before making any changes to capture the pre-change snapshot
        public void RecordUndo(string operationName)
        {
            // Serialize current state BEFORE changes so undo restores this snapshot
            if (document != null)
            {
                serializedState = JsonUtility.ToJson(new DocumentState(document), true);
            }
            // Register a complete snapshot for Undo system
            Undo.RegisterCompleteObjectUndo(this, operationName);
            EditorUtility.SetDirty(this);
        }

        // Call after applying changes so redo has a post-change snapshot
        public void CapturePostState()
        {
            if (document != null)
            {
                serializedState = JsonUtility.ToJson(new DocumentState(document), true);
                EditorUtility.SetDirty(this);
            }
        }
        
        // Restore document from serialized state after undo/redo
        public void RestoreFromSerializedState()
        {
            if (!string.IsNullOrEmpty(serializedState) && document != null)
            {
                var state = JsonUtility.FromJson<DocumentState>(serializedState);
                state.ApplyToDocument(document);
            }
        }
        
        // Helper class to serialize full document state (including structure)
        [System.Serializable]
        private class DocumentState
        {
            [System.Serializable]
            public class SerializedEntry
            {
                public string Group;
                public string Pid;
                public bool IsHeader;
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
                public int[] States;
                [System.Serializable]
                public class SerializedItem
                {
                    public string Iid;
                    public int Drop;
                }
                
                public SerializedItem[] Items;

                // AI
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

                // Additional attributes captured from XML
                public string[] AdditionalKeys;
                public string[] AdditionalValues;
                
                public SerializedEntry() { }
                
                public SerializedEntry(DisposEntry entry)
                {
                    Group = entry.Group;
                    Pid = entry.Pid;
                    IsHeader = entry.IsGroupHeader;
                    Force = entry.Force;
                    Flag = entry.Flag;
                    AppearX = entry.AppearX;
                    AppearY = entry.AppearY;
                    DisposX = entry.DisposX;
                    DisposY = entry.DisposY;
                    Direction = entry.Direction;
                    LevelN = entry.LevelN;
                    LevelH = entry.LevelH;
                    LevelL = entry.LevelL;
                    Jid = entry.Jid;
                    Sid = entry.Sid;
                    Bid = entry.Bid;
                    Gid = entry.Gid;
                    HpStockCount = entry.HpStockCount;
                    States = entry.States != null ? entry.States.ToArray() : null;
                    
                    // Copy items
                    Items = new SerializedItem[entry.Items.Length];
                    for (int i = 0; i < entry.Items.Length; i++)
                    {
                        if (entry.Items[i] != null)
                        {
                            Items[i] = new SerializedItem
                            {
                                Iid = entry.Items[i].Iid,
                                Drop = entry.Items[i].Drop
                            };
                        }
                    }

                    // AI block
                    AI_ActionName = entry.AI_ActionName;
                    AI_ActionVal = entry.AI_ActionVal;
                    AI_MindName = entry.AI_MindName;
                    AI_MindVal = entry.AI_MindVal;
                    AI_AttackName = entry.AI_AttackName;
                    AI_AttackVal = entry.AI_AttackVal;
                    AI_MoveName = entry.AI_MoveName;
                    AI_MoveVal = entry.AI_MoveVal;
                    AI_BattleRate = entry.AI_BattleRate;
                    AI_Priority = entry.AI_Priority;
                    AI_HealRateA = entry.AI_HealRateA;
                    AI_HealRateB = entry.AI_HealRateB;
                    AI_BandNo = entry.AI_BandNo;
                    AI_MoveLimit = entry.AI_MoveLimit;
                    AI_Flag = entry.AI_Flag;
                    AI_Active = entry.AI_Active;
                    AI_ActiveTurn = entry.AI_ActiveTurn;
                    AI_ActiveFlag = entry.AI_ActiveFlag;

                    // Additional attributes
                    var add = entry.GetAllAdditionalAttributes();
                    if (add != null && add.Count > 0)
                    {
                        AdditionalKeys = add.Keys.ToArray();
                        AdditionalValues = add.Values.ToArray();
                    }
                }
                
                public void ApplyTo(DisposEntry entry)
                {
                    entry.Group = Group;
                    entry.Pid = Pid;
                    entry.Force = Force;
                    entry.Flag = Flag;
                    entry.AppearX = AppearX;
                    entry.AppearY = AppearY;
                    entry.DisposX = DisposX;
                    entry.DisposY = DisposY;
                    entry.Direction = Direction;
                    entry.LevelN = LevelN;
                    entry.LevelH = LevelH;
                    entry.LevelL = LevelL;
                    entry.Jid = Jid;
                    entry.Sid = Sid;
                    entry.Bid = Bid;
                    entry.Gid = Gid;
                    entry.HpStockCount = HpStockCount;
                    if (States != null && States.Length == entry.States.Length)
                    {
                        for (int i = 0; i < States.Length; i++) entry.States[i] = States[i];
                    }
                    
                    // Copy items back
                    if (Items != null)
                    {
                        for (int i = 0; i < entry.Items.Length && i < Items.Length; i++)
                        {
                            if (Items[i] != null)
                            {
                                if (entry.Items[i] == null)
                                    entry.Items[i] = new DisposEntry.ItemData();
                                    
                                entry.Items[i].Iid = Items[i].Iid;
                                entry.Items[i].Drop = Items[i].Drop;
                            }
                        }
                    }

                    // AI block
                    entry.AI_ActionName = AI_ActionName;
                    entry.AI_ActionVal = AI_ActionVal;
                    entry.AI_MindName = AI_MindName;
                    entry.AI_MindVal = AI_MindVal;
                    entry.AI_AttackName = AI_AttackName;
                    entry.AI_AttackVal = AI_AttackVal;
                    entry.AI_MoveName = AI_MoveName;
                    entry.AI_MoveVal = AI_MoveVal;
                    entry.AI_BattleRate = AI_BattleRate;
                    entry.AI_Priority = AI_Priority;
                    entry.AI_HealRateA = AI_HealRateA;
                    entry.AI_HealRateB = AI_HealRateB;
                    entry.AI_BandNo = AI_BandNo;
                    entry.AI_MoveLimit = AI_MoveLimit;
                    entry.AI_Flag = AI_Flag;
                    entry.AI_Active = AI_Active;
                    entry.AI_ActiveTurn = AI_ActiveTurn;
                    entry.AI_ActiveFlag = AI_ActiveFlag;

                    // Additional attributes
                    if (AdditionalKeys != null && AdditionalValues != null)
                    {
                        int n = Mathf.Min(AdditionalKeys.Length, AdditionalValues.Length);
                        for (int i = 0; i < n; i++)
                        {
                            entry.SetAdditionalAttribute(AdditionalKeys[i], AdditionalValues[i]);
                        }
                    }
                }
            }
            
            public SerializedEntry[] entries;
            
            public DocumentState() { }
            
            public DocumentState(DisposDocument doc)
            {
                var list = new System.Collections.Generic.List<SerializedEntry>();
                // Preserve full structure order by using AllEntries (contains group headers)
                foreach (var entry in doc.AllEntries)
                {
                    list.Add(new SerializedEntry(entry));
                }
                entries = list.ToArray();
            }
            
            public void ApplyToDocument(DisposDocument doc)
            {
                // Rebuild full structure from serialized snapshot
                if (entries == null) return;

                doc.AllEntries.Clear();
                doc.Groups.Clear();

                DisposGroup currentGroup = null;
                foreach (var se in entries)
                {
                    var entry = new DisposEntry();
                    // Initialize defaults for arrays
                    // Apply serialized fields back onto the new entry
                    se.ApplyTo(entry);
                    entry.IsGroupHeader = se.IsHeader || (!string.IsNullOrEmpty(entry.Group) && string.IsNullOrEmpty(entry.Pid));

                    doc.AllEntries.Add(entry);

                    if (entry.IsGroupHeader)
                    {
                        currentGroup = new DisposGroup
                        {
                            GroupName = entry.Group,
                            Entries = new System.Collections.Generic.List<DisposEntry>(),
                            IsExpanded = EditorPrefs.GetBool($"Dispos_Group_{entry.Group}_Expanded", true),
                            IsVisible = EditorPrefs.GetBool($"Dispos_Group_{entry.Group}_Visible", true)
                        };
                        doc.Groups.Add(currentGroup);
                    }
                    else if (currentGroup != null)
                    {
                        // Ensure group field matches
                        entry.Group = currentGroup.GroupName;
                        currentGroup.Entries.Add(entry);
                    }
                }
            }
        }
    }
}
