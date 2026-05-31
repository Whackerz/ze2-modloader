Example - Custom Map Assets

Demonstrates a map with mod-local runtime PNG textures:
- Tilesheet points at a 512x512 PNG used as Global.MasterEnvTex while this map is active.
- LightTexture points at a 512x512 PNG used as Global.CurrentLevelLightTex.
- ExampleAssetMap_Shadow.png is also present to demonstrate the automatic <Prefix>_Shadow.png override path.

The sample tilesheet is intentionally artificial so authors can immediately see when the custom sheet is being used.
