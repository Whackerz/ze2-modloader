using dnlib.DotNet;
using dnlib.DotNet.Emit;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ZE2.BepInExCorePatcher <path-to-ZE2.ModLoader.dll>");
    return 2;
}

string dllPath = Path.GetFullPath(args[0]);
if (!File.Exists(dllPath))
{
    Console.Error.WriteLine("File not found: " + dllPath);
    return 2;
}

string backupPath = dllPath + ".pre_direct_maps.bak";
if (!File.Exists(backupPath))
{
    File.Copy(dllPath, backupPath);
    Console.WriteLine("Backed up original DLL to " + backupPath);
}
else
{
    Console.WriteLine("Backup already exists: " + backupPath);
}

string tempPath = dllPath + ".patched.tmp";
using (ModuleDefMD module = ModuleDefMD.Load(dllPath))
{
    TypeDef pluginType = module.Types.FirstOrDefault(t => t.FullName == "ZE2.ModLoader.Plugin")
        ?? throw new InvalidOperationException("Could not find ZE2.ModLoader.Plugin.");

MethodDef applyMaps = pluginType.Methods.FirstOrDefault(m => m.Name == "ApplyMaps")
    ?? throw new InvalidOperationException("Could not find Plugin.ApplyMaps.");
MethodDef applyRawFile = pluginType.Methods.FirstOrDefault(m => m.Name == "ApplyRawFile")
    ?? throw new InvalidOperationException("Could not find Plugin.ApplyRawFile.");

    PatchIntReturnZero(applyMaps);
    PatchIntReturnZero(applyRawFile);

    module.Write(tempPath);
}

File.Copy(tempPath, dllPath, true);
File.Delete(tempPath);
Console.WriteLine("Patched Plugin.ApplyMaps and Plugin.ApplyRawFile to skip Data/* staging.");
return 0;

static void PatchIntReturnZero(MethodDef method)
{
    method.Body ??= new CilBody();
    method.Body.ExceptionHandlers.Clear();
    method.Body.Variables.Clear();
    method.Body.Instructions.Clear();
    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    method.Body.KeepOldMaxStack = false;
}
