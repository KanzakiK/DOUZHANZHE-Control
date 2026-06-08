using System.Diagnostics;
using System.Security.Principal;

namespace Douzhanzhe.Shell;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 检查是否以管理员权限运行，不是则自动提权重启
        if (!args.Any(a => a == "--elevated"))
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                try
                {
                    var exePath = Application.ExecutablePath;
                    var allArgs = string.Join(" ", args.Select(a => $"\"{a}\""));
                    var psi = new ProcessStartInfo(exePath)
                    {
                        Verb = "runas",
                        UseShellExecute = true,
                        Arguments = string.IsNullOrWhiteSpace(allArgs) ? "--elevated" : allArgs + " --elevated"
                    };
                    Process.Start(psi);
                }
                catch
                {
                    // 提权失败（用户拒绝或策略限制），仍尝试以当前权限运行
                    ApplicationConfiguration.Initialize();
                    Application.Run(new Form1());
                }
                return;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}