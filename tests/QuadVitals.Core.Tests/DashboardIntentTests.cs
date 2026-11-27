using QuadVitals.Core;

namespace QuadVitals.Core.Tests
{
    internal static partial class Program
    {
        /// <summary>
        /// The command line is the only thing a second dashboard launch can send to the running
        /// window, so the mapping has to be stable: an unknown switch must never reach the window
        /// as a page key, and a hand-written page name must fall back instead of failing.
        /// </summary>
        private static void DashboardIntentBehaviour()
        {
            Equal(DashboardIntentKind.None, DashboardIntent.Parse(null), "空参数只激活窗口");
            Equal(DashboardIntentKind.None, DashboardIntent.Parse(new string[0]), "空命令行只激活窗口");
            Equal(DashboardIntentKind.None, DashboardIntent.Parse(new[] { "--new-instance" }), "强制新窗口不带页面意图");

            Equal(DashboardIntentKind.Feedback, DashboardIntent.Parse(new[] { "--feedback" }), "--feedback 打开反馈面板");
            Equal(DashboardIntentKind.General, DashboardIntent.Parse(new[] { "--general" }), "--general 打开通用设置");
            Equal(DashboardIntentKind.AgentStatus, DashboardIntent.Parse(new[] { "--agent-status" }), "--agent-status 打开传感器面板");
            Equal(DashboardIntentKind.About, DashboardIntent.Parse(new[] { "--about" }), "--about 切到关于页");

            Equal(DashboardIntentKind.Overview, DashboardIntent.Parse(new[] { "--page=overview" }), "显式概览页");
            Equal(DashboardIntentKind.Elements, DashboardIntent.Parse(new[] { "--page=elements" }), "显示项页");
            Equal(DashboardIntentKind.About, DashboardIntent.Parse(new[] { "--page=about" }), "关于页");
            Equal(DashboardIntentKind.Overview, DashboardIntent.Parse(new[] { "--page=nonsense" }), "未知页回到概览");
            Equal(DashboardIntentKind.Overview, DashboardIntent.Parse(new[] { "--page=feedback" }), "面板不是页，回到概览");
            Equal(DashboardIntentKind.Overview, DashboardIntent.Parse(new[] { "--page=" }), "空页名回到概览");
            Equal(DashboardIntentKind.About, DashboardIntent.Parse(new[] { "--page=elements", "--about" }), "后写的开关生效");
            Equal(DashboardIntentKind.Feedback, DashboardIntent.Parse(new[] { "--about", "--feedback" }), "面板可以压过页面");

            Equal("overview", DashboardIntent.PageKey(DashboardIntentKind.None), "无意图落在概览");
            Equal("overview", DashboardIntent.PageKey(DashboardIntentKind.Feedback), "面板背后是概览");
            Equal("elements", DashboardIntent.PageKey(DashboardIntentKind.Elements), "显示项页键");
            Equal("about", DashboardIntent.PageKey(DashboardIntentKind.About), "关于页键");

            True(DashboardIntent.IsNewInstance(new[] { "--new-instance" }), "识别强制新窗口开关");
            True(DashboardIntent.IsNewInstance(new[] { "--feedback", "--new-instance" }), "开关可以跟在别的参数后面");
            True(!DashboardIntent.IsNewInstance(new[] { "--feedback" }), "普通启动不强制新窗口");
            True(!DashboardIntent.IsNewInstance(null), "空参数不强制新窗口");

            True(DashboardIntent.Has(new[] { "--diagnostics" }, "--diagnostics"), "诊断开关独立于仪表盘意图");
            True(!DashboardIntent.Has(null, "--diagnostics"), "空参数没有诊断开关");
            True(!DashboardIntent.Has(new[] { "--diagnostics" }, "--feedback"), "只匹配完整开关名");
        }
    }
}
