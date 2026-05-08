using System;
using System.Collections.Generic;
using UnityEngine;

namespace DivineDragon.MapTools
{
    /// <summary>
    /// Project-scoped, editor-only color overrides for terrain tiles whose in-game colors are
    /// hard to distinguish. The asset lives under Assets/Editor so it never ships in a build.
    /// </summary>
    public class TileColorOverrides : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string tid;
            public Color color = Color.white;
        }

        [SerializeField]
        private List<Entry> entries = new List<Entry>();

        public List<Entry> Entries => entries;
    }
}
