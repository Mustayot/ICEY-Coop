using System;
using IceyCoop.Bootstrap;

internal static class BootstrapPathResolverSpecs
{
    private static int assertions;

    private static int Main()
    {
        try
        {
            TestDefaultPathsAcrossDrivesAndUnicode();
            TestExplicitEnvironmentOverride();
            TestInvalidAssemblyLocationHasNoFallback();
            Console.WriteLine("PASS BootstrapPathResolver assertions=" + assertions);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL BootstrapPathResolver: " + ex);
            return 1;
        }
    }

    private static void TestDefaultPathsAcrossDrivesAndUnicode()
    {
        AssertDefault("C:\\Program Files (x86)\\Steam\\steamapps\\common\\ICEY\\BepInEx\\plugins\\IceyCoop.Bootstrap.dll",
            "C:\\Program Files (x86)\\Steam\\steamapps\\common\\ICEY");
        AssertDefault("E:\\Games and Mods\\冰艾 合作版\\BepInEx\\plugins\\IceyCoop.Bootstrap.dll",
            "E:\\Games and Mods\\冰艾 合作版");
    }

    private static void AssertDefault(string assemblyPath, string expectedGameRoot)
    {
        string bepInEx = System.IO.Path.Combine(expectedGameRoot, "BepInEx");
        string project = BootstrapPathResolver.ResolveProjectRoot(string.Empty,
            BootstrapPathResolver.ResolveBepInExRoot(assemblyPath));
        Equal(System.IO.Path.Combine(bepInEx, "icey-coop"), project, "default project root");
        Equal(expectedGameRoot, BootstrapPathResolver.ResolveGameRoot(bepInEx), "game root");
        Equal(System.IO.Path.Combine(project, "logs"), BootstrapPathResolver.ResolveLogRoot(project), "log root");
    }

    private static void TestExplicitEnvironmentOverride()
    {
        const string variable = "ICEYCOOP_PROJECT_ROOT";
        string previous = Environment.GetEnvironmentVariable(variable);
        const string assemblyPath = "D:\\Steam Library\\ICEY 游戏\\BepInEx\\plugins\\IceyCoop.Bootstrap.dll";
        const string overridePath = "Z:\\Dev Space\\合作工程\\Icey Coop";
        try
        {
            Environment.SetEnvironmentVariable(variable, overridePath);
            string configuredRoot = Environment.GetEnvironmentVariable(variable);
            string bepInEx = BootstrapPathResolver.ResolveBepInExRoot(assemblyPath);
            Equal(overridePath, BootstrapPathResolver.ResolveProjectRoot(configuredRoot, bepInEx),
                "explicit ICEYCOOP_PROJECT_ROOT remains unchanged");
            Equal("D:\\Steam Library\\ICEY 游戏", BootstrapPathResolver.ResolveGameRoot(bepInEx),
                "game root remains tied to plugin assembly");
            Equal(System.IO.Path.Combine(overridePath, "logs"),
                BootstrapPathResolver.ResolveLogRoot(BootstrapPathResolver.ResolveProjectRoot(configuredRoot, bepInEx)),
                "explicit override log root");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static void TestInvalidAssemblyLocationHasNoFallback()
    {
        ThrowsInvalidOperation(delegate
        {
            BootstrapPathResolver.ResolveBepInExRoot("C:\\Games\\ICEY\\Other\\IceyCoop.Bootstrap.dll");
        }, "unexpected plugin layout rejected");
        ThrowsInvalidOperation(delegate
        {
            BootstrapPathResolver.ResolveProjectRoot(string.Empty, null);
        }, "missing BepInEx root rejected");
    }

    private static void Equal(string expected, string actual, string name)
    {
        assertions++;
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(name + " expected <" + expected + "> actual <" + actual + ">");
    }

    private static void ThrowsInvalidOperation(Action action, string name)
    {
        assertions++;
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(name + " did not throw InvalidOperationException");
    }
}
