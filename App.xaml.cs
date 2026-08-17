using System.Windows;
using Bigfile.Behaviors;

namespace Bigfile;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Done before any window exists: animation metadata can only be changed
        // before the property is first used.
        Display.UseFullRefreshRate();

        base.OnStartup(e);
    }
}
