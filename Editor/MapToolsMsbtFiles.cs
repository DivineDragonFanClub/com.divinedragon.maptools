using DivineDragon.Msbt.Editor;

namespace DivineDragon.MapTools
{
    /// <summary>
    /// Maptools' game-specific knowledge of which MSBT files own which keys.
    /// Lives in maptools (not msbt) because the routing is Engage-specific.
    /// </summary>
    internal static class MapToolsMsbtFiles
    {
        public static readonly FileId GameData = new FileId("GameData");

        // Chapter titles (MCID_*) are scattered: M-series in base, G-series spread across
        // Patch0..Patch2 by release wave, E-series in Patch3. The split isn't predictable
        // from the prefix letter alone (e.g., G002/G004/G005 are in Patch1, G003/G006 in
        // Patch2), so callers consult this short list rather than hardcode per-CID routing.
        private static readonly FileId[] ChapterCandidates =
        {
            GameData,
            new FileId("Patch0"),
            new FileId("Patch1"),
            new FileId("Patch2"),
            new FileId("Patch3"),
        };

        /// <summary>
        /// Returns the localized string for a chapter MCID by checking GameData and every
        /// patch in turn. Returns <paramref name="mcid"/> as a fallback when not found.
        /// </summary>
        public static string GetChapterString(string mcid, Language lang)
        {
            if (string.IsNullOrEmpty(mcid))
            {
                return mcid;
            }

            var id = new MessageId(mcid);
            foreach (FileId candidate in ChapterCandidates)
            {
                string value = MsbtProvider.Get(candidate, id, lang);
                if (value != mcid)
                {
                    return value;
                }
            }
            return mcid;
        }
    }
}
