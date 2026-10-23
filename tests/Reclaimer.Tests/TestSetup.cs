using Reclaimer.Shell;

namespace Reclaimer.Tests;

[TestClass]
public static class TestSetup
{
    /// <summary>Tests of corrupt files log warnings; they must not create %APPDATA%\Reclaimer\logs on the machine.</summary>
    [AssemblyInitialize]
    public static void Init(TestContext _) => Log.Off = true;
}
