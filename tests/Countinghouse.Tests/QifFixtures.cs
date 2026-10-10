using System.Reflection;

namespace Countinghouse.Tests;

// The hand-written .qif samples under Fixtures/, copied next to the test assembly at build time.
// The real Quicken exports are never used in tests — they hold 20 years of actual payee and
// salary history and are deliberately not committed.
internal static class QifFixtures
{
    public static string Read(string fileName)
    {
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        return File.ReadAllText(Path.Combine(directory, "Fixtures", fileName));
    }
}
