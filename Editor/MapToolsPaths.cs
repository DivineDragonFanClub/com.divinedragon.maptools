using Dragonstone;

namespace DivineDragon.MapTools
{
    /// <summary>
    /// Centralized path definitions for map tools.
    /// All game data paths are derived from EngageAddressableSettings.GameBuildPath.
    /// </summary>
    public static class MapToolsPaths
    {
        // Game dump paths (derived from user-configured settings)
        public static string GameBuildPath => EngageAddressableSettings.GameBuildPath;
        public static string GameDataAssetRoot => GameBuildPath + "/fe_assets_gamedata";
        public static string TerrainDirectory => GameDataAssetRoot + "/terrains";
        public static string DisposDirectory => GameDataAssetRoot + "/dispos";
        public static string TerrainXmlBundlePath => GameDataAssetRoot + "/terrain.xml.bundle";
        public static string ChapterBundlePath => GameDataAssetRoot + "/chapter.xml.bundle";

        // Project asset paths (constant)
        public const string GameDataShareAssetPath = "Assets/Share/Addressables/Patch/Patch3/GameData";
        public const string ChapterAssetPath = GameDataShareAssetPath + "/Chapter.xml";

        /// <summary>
        /// Returns true if the game data paths are configured.
        /// </summary>
        public static bool IsConfigured => !string.IsNullOrEmpty(GameBuildPath);
    }
}
