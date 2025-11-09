# Getting started

This currently requires a custom branch of the dumper. 

Use 
```
"com.divinedragon.dumper": "https://github.com/DivineDragonFanClub/com.divinedragon.dumper.git#fbxify-and-multi",
```

Start the tool by going to `Map Tools` -> `Terrain Paint Tool`.

You should see a button asking to dump the `Terrain.xml` if it isn't already in the default place (`Assets/Share/Addressables/Patch/Patch3/GameData/Terrain.xml`)in your project. If you're using custom terrain types, you'll need to provide your own `Terrain.xml`.

After that, you'll be able to choose a terrain tile asset to paint with.

Then just paint in the editor window. Undo support is present but has not been extensively tested with interacting with other potential undo stacks.

Also, any changes to the terrain tile asset need to be saved manually, with a ctrl/command S or by clicking the button.

# Other notes
I've observed that the terrain tile entries in the terrain array are saved in quotes in the `MapTerrain` asset YAML file - this is as far as I can tell, default Unity Editor behavior and is harmless in-game. The ones that come with the game do _not_ have quotes. 