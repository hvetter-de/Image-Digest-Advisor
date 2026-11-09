using Velopack;

namespace ImageDigestAdvisor.App;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks (and exits for them),
        // and applies an already downloaded update on startup. Does nothing in MSIX/dev builds.
        VelopackApp.Build().Run();

        XamlGeneratedProgram.XamlGeneratedMain();
    }
}
