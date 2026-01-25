var asmPath = @"c:\Work\backup\tcpaop\Tests\ProtoTestTool\bin\Debug\net9.0-windows\Wpf.Ui.dll";
var asm = System.Reflection.Assembly.LoadFrom(asmPath);
var resourceNames = asm.GetManifestResourceNames();

foreach (var resName in resourceNames)
{
    Console.WriteLine($"Resource: {resName}");
    if (resName.EndsWith(".g.resources"))
    {
        using var stream = asm.GetManifestResourceStream(resName);
        using var reader = new System.Resources.ResourceReader(stream);
        foreach (System.Collections.DictionaryEntry entry in reader)
        {
            Console.WriteLine($"  - {entry.Key}");
        }
    }
}
