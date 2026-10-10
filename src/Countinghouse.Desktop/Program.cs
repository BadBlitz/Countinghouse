using Velopack;

namespace Countinghouse.Desktop;

/// <summary>
/// A hand-written entry point instead of the one WPF generates from <c>App.xaml</c>, because
/// Velopack has to run before anything else.
/// </summary>
/// <remarks>
/// The installer and updater launch this same exe with hook arguments (install, update,
/// uninstall) and expect it to exit immediately; <see cref="VelopackApp.Run"/> handles those and
/// returns only for a normal launch. It also applies an update that was downloaded but never
/// applied — the "installs next launch" half of the update banner. Running it after WPF starts
/// would open a window for a process that was only ever meant to answer a hook.
/// </remarks>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
