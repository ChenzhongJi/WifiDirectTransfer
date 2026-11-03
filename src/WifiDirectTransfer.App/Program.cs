using System.Runtime.InteropServices;

namespace WifiDirectTransfer.App;

internal static class Program
{
    private const string AppUserModelId = "WifiDirectTransfer.M0";

    [STAThread]
    private static void Main()
    {
        StartupLog.Write("Program.Main executed.");

        var hresult = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (hresult != 0)
        {
            throw new InvalidOperationException(
                "无法初始化应用的 Wi-Fi Direct 标识，设备发现功能不可用。请重启应用后重试。")
            {
                HResult = hresult
            };
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);
}
