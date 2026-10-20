using Velopack;

namespace OnlyShell.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Velopack must process install/update hooks before WPF initializes.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
