namespace ZE2.ModLoader
{
    public interface IZe2Mod
    {
        string Name { get; }
        string Version { get; }
        void OnLoad();
    }
}
